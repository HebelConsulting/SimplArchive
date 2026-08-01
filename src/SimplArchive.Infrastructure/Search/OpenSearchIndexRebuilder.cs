using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Search;

// Full search-index rebuild (ADR 0139): builds a brand-new OpenSearch index from Postgres, then atomically
// flips the "documents" alias to it (blue-green) and drops the old index — no search downtime, and it
// backfills documents that predate indexing or were missed while OpenSearch was down (ADR 0253's deferred
// reindex-all). Cross-tenant: enumerates every tenant's active documents and sets the current tenant per
// document so the (tenant-filtered) content queries resolve.
public sealed class OpenSearchIndexRebuilder
{
    private const string Alias = "documents";

    private readonly HttpClient _http;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly CurrentTenantAccessor _tenantAccessor;
    private readonly IObjectStorageClient _storage;
    private readonly ITextExtractor _extractor;
    private readonly IArchiveReader _archiveReader;
    private readonly IEffectiveRightsCalculator _rightsCalculator;
    private readonly ILogger<OpenSearchIndexRebuilder> _logger;

    public OpenSearchIndexRebuilder(
        HttpClient http, SimplArchiveDbContext dbContext, CurrentTenantAccessor tenantAccessor,
        IObjectStorageClient storage, ITextExtractor extractor, IArchiveReader archiveReader,
        IEffectiveRightsCalculator rightsCalculator, ILogger<OpenSearchIndexRebuilder> logger)
    {
        _http = http;
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _storage = storage;
        _extractor = extractor;
        _archiveReader = archiveReader;
        _rightsCalculator = rightsCalculator;
        _logger = logger;
    }

    public async Task<bool> AliasExistsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"_alias/{Alias}", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<int> RebuildAsync(CancellationToken cancellationToken)
    {
        var newIndex = $"documents-{Guid.NewGuid():N}";
        await CreateIndexAsync(newIndex, cancellationToken);

        var documents = await _dbContext.Documents
            .IgnoreQueryFilters(["TenantFilter"]) // every tenant; soft-delete filter still excludes deleted docs
            .Select(d => new { d.Id, d.TenantId })
            .ToListAsync(cancellationToken);

        var count = 0;
        foreach (var document in documents)
        {
            _tenantAccessor.TenantId = document.TenantId;
            var body = await BuildBodyAsync(document.Id, cancellationToken);
            if (body is null)
            {
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Put, $"{newIndex}/_doc/{document.Id}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                count++;
            }
            else
            {
                _logger.LogWarning("Reindex of {Document} returned {Status}.", document.Id, response.StatusCode);
            }
        }

        using (var refresh = new HttpRequestMessage(HttpMethod.Post, $"{newIndex}/_refresh"))
        {
            using var _ = await _http.SendAsync(refresh, cancellationToken);
        }

        await SwapAliasAsync(newIndex, cancellationToken);
        _logger.LogInformation("Search index rebuild complete: {Count} documents into {Index}.", count, newIndex);
        return count;
    }

    private async Task CreateIndexAsync(string index, CancellationToken cancellationToken)
    {
        var mapping = JsonSerializer.Serialize(new
        {
            mappings = new
            {
                properties = new
                {
                    tenantId = new { type = "keyword" },
                    repositoryId = new { type = "keyword" },
                    parentId = new { type = "keyword" },
                    indexedVersionId = new { type = "keyword" },
                    isFolder = new { type = "boolean" },
                    name = new { type = "text" },
                    indexValues = new { type = "text" },
                    content = new { type = "text" },
                    allowedPrincipals = new { type = "keyword" },
                    // Typed index-field values for filtering (ADR 0043). Nested so a filter matches name +
                    // typed value on the *same* field; text always present, number/date/bool when parseable.
                    fields = new
                    {
                        type = "nested",
                        properties = new
                        {
                            name = new { type = "keyword" },
                            text = new { type = "keyword" },
                            number = new { type = "double" },
                            date = new { type = "date" },
                            @bool = new { type = "boolean" },
                        },
                    },
                    // System fields (ADR "System-field search"): document/version dates + resolved creator
                    // names + the issuing date.
                    createdAt = new { type = "date" },
                    documentType = new { type = "keyword" },
                    createdBy = new { type = "keyword" },
                    versionCreatedAt = new { type = "date" },
                    versionCreatedBy = new { type = "keyword" },
                    documentDate = new { type = "date" },
                    // Sensitivity label (ADR "Data classification / sensitivity labels").
                    sensitivityLabel = new { type = "keyword" },
                    // Numeric label rank for the clearance-ceiling filter (ADR "Sensitivity clearance enforcement").
                    sensitivityRank = new { type = "integer" },
                    // Free-form tags (ADR "Document tags").
                    tags = new { type = "keyword" },
                    // Facet dimensions (ADR "Search facet refinements"): the current version's file extension
                    // and its document-date year, both keyword for terms faceting + system[..][in] drill-down.
                    fileType = new { type = "keyword" },
                    documentYear = new { type = "keyword" },
                },
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Put, index)
        {
            Content = new StringContent(mapping, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SwapAliasAsync(string newIndex, CancellationToken cancellationToken)
    {
        // Slice 1 created a concrete index literally named "documents"; drop it so the alias name is free.
        await DeleteConcreteAliasNameIndexAsync(cancellationToken);

        var oldIndices = new List<string>();
        using (var get = await _http.GetAsync($"_alias/{Alias}", cancellationToken))
        {
            if (get.IsSuccessStatusCode)
            {
                var json = await get.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                foreach (var property in json.EnumerateObject())
                {
                    oldIndices.Add(property.Name);
                }
            }
        }

        var actions = new List<object>();
        foreach (var old in oldIndices)
        {
            actions.Add(new { remove = new { index = old, alias = Alias } });
        }
        actions.Add(new { add = new { index = newIndex, alias = Alias } });

        var body = JsonSerializer.Serialize(new { actions });
        using (var post = new HttpRequestMessage(HttpMethod.Post, "_aliases")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        })
        {
            using var response = await _http.SendAsync(post, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        foreach (var old in oldIndices.Where(i => i != newIndex))
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, old);
            using var _ = await _http.SendAsync(delete, cancellationToken);
        }
    }

    private async Task DeleteConcreteAliasNameIndexAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"_cat/indices/{Alias}?format=json&h=index", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (json.EnumerateArray().Any(e => e.TryGetProperty("index", out var i) && i.GetString() == Alias))
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, Alias);
            using var _ = await _http.SendAsync(delete, cancellationToken);
        }
    }

    private async Task<string?> BuildBodyAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var doc = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Name, d.TenantId, d.ParentId, d.CreatedAt, d.CreatedByUserId, d.CreatedByServiceAccountId, d.MaskVersionId, d.CurrentVersionId, SensitivityLabelName = d.SensitivityLabelId == null ? null : _dbContext.SensitivityLabelDefinitions.IgnoreQueryFilters().Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Name).FirstOrDefault(), SensitivityLabelRank = d.SensitivityLabelId == null ? (int?)null : _dbContext.SensitivityLabelDefinitions.IgnoreQueryFilters().Where(l => l.Id == d.SensitivityLabelId).Select(l => (int?)l.Rank).FirstOrDefault() })
            .SingleOrDefaultAsync(cancellationToken);

        if (doc is null)
        {
            return null;
        }

        // The document type facet (ADR "Search facets") — the assigned mask version's name (null when unclassified).
        var documentType = doc.MaskVersionId is { } maskVersionId
            ? await _dbContext.MaskVersions.Where(mv => mv.Id == maskVersionId).Select(mv => mv.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        var fieldData = await _dbContext.FieldValues
            .Where(fv => fv.DocumentId == documentId)
            .Join(_dbContext.FieldDefinitions, fv => fv.FieldDefinitionId, fd => fd.Id,
                (fv, fd) => new { fd.Name, fd.DataType, fv.Value })
            .ToListAsync(cancellationToken);

        var indexValues = fieldData.Select(f => f.Value).ToList();
        var typedFields = SearchFieldMapper.BuildTypedFields(fieldData.Select(f => (f.Name, f.DataType, f.Value)));

        // Current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
        var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, doc.CurrentVersionId, cancellationToken);

        var repositoryId = await ResolveRootAsync(documentId, cancellationToken);
        var allowedPrincipals = await _rightsCalculator.GetVisibilityPrincipalsAsync(documentId, cancellationToken);
        var tags = await _dbContext.DocumentTags.Where(t => t.DocumentId == documentId).Select(t => t.Tag).ToListAsync(cancellationToken);

        var createdBy = await ResolveCreatorNameAsync(doc.CreatedByUserId, doc.CreatedByServiceAccountId, cancellationToken);
        var versionCreatedBy = version is null ? null
            : await ResolveCreatorNameAsync(version.CreatedByUserId, version.CreatedByServiceAccountId, cancellationToken);

        var content = "";
        if (version is not null)
        {
            try
            {
                if (ArchiveContentExtractor.IsZip(version.ObjectKey))
                {
                    // A .zip's first-level entry names + text, one archive deep (ADR "Zip file browsing").
                    content = await ArchiveContentExtractor.ExtractAsync(_storage, _archiveReader, _extractor, version.ObjectKey, cancellationToken);
                }
                else
                {
                    await using var stream = await _storage.GetObjectAsync(version.ObjectKey, cancellationToken);
                    content = await _extractor.ExtractAsync(stream, "application/octet-stream", cancellationToken);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Content extraction failed for {Document} during rebuild.", documentId);
            }
        }

        return JsonSerializer.Serialize(new
        {
            tenantId = doc.TenantId,
            repositoryId,
            parentId = doc.ParentId,
            indexedVersionId = version?.Id,
            isFolder = version is null,
            name = doc.Name,
            indexValues = string.Join(" ", indexValues),
            content,
            allowedPrincipals,
            fields = typedFields,
            createdAt = doc.CreatedAt,
            createdBy,
            versionCreatedAt = version?.CreatedAt,
            versionCreatedBy,
            documentDate = version is null ? (DateOnly?)null : version.DocumentDate,
            documentType,
            sensitivityLabel = doc.SensitivityLabelName,
            sensitivityRank = doc.SensitivityLabelRank ?? 0,
            tags,
            // Facet dimensions (ADR "Search facet refinements") — null for folders (no version).
            fileType = version is null ? null : SearchFieldMapper.FileType(version.ObjectKey),
            documentYear = version is null ? null : version.DocumentDate.Year.ToString(CultureInfo.InvariantCulture),
        });
    }

    private async Task<string?> ResolveCreatorNameAsync(Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        if (userId is { } id)
        {
            return await _dbContext.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken);
        }

        if (serviceAccountId is { } serviceId)
        {
            return await _dbContext.ServiceAccounts.Where(s => s.Id == serviceId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }

    private async Task<Guid> ResolveRootAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var currentId = documentId;
        while (true)
        {
            var parentId = await _dbContext.Documents
                .Where(d => d.Id == currentId)
                .Select(d => d.ParentId)
                .SingleAsync(cancellationToken);

            if (parentId is not { } parent)
            {
                return currentId;
            }

            currentId = parent;
        }
    }
}
