using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Search;

// Keeps a document's OpenSearch entry in sync (ADR "OpenSearch full-text slice 1"). Best-effort — every
// method swallows its own errors so a search-engine hiccup never fails a write. Re-extracts document
// content only when the latest confirmed version changed (otherwise reuses the already-indexed text), so
// metadata-only edits (rename, index-data) don't re-download+re-extract.
public sealed class OpenSearchDocumentIndexer : IDocumentIndexer
{
    // "documents" is an alias (not a real index) — the rebuilder creates the backing index and points the
    // alias at it (blue-green, ADR 0139). Per-doc writes go through the alias, but only once it exists (else
    // a write would auto-create a plain "documents" index that then blocks the alias) — the rebuild backfills
    // anything skipped before then.
    private const string Alias = "documents";
    private static bool _aliasReady;

    private readonly HttpClient _http;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _storage;
    private readonly ITextExtractor _extractor;
    private readonly IArchiveReader _archiveReader;
    private readonly IEffectiveRightsCalculator _rightsCalculator;
    private readonly ILogger<OpenSearchDocumentIndexer> _logger;

    public OpenSearchDocumentIndexer(
        HttpClient http, SimplArchiveDbContext dbContext, IObjectStorageClient storage,
        ITextExtractor extractor, IArchiveReader archiveReader, IEffectiveRightsCalculator rightsCalculator,
        ILogger<OpenSearchDocumentIndexer> logger)
    {
        _http = http;
        _dbContext = dbContext;
        _storage = storage;
        _extractor = extractor;
        _archiveReader = archiveReader;
        _rightsCalculator = rightsCalculator;
        _logger = logger;
    }

    public async Task<bool> SyncAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await AliasReadyAsync(cancellationToken))
            {
                return false; // the alias doesn't exist yet — retry once the rebuild creates it
            }

            // Default query filters apply: a soft-deleted or cross-tenant document reads as null → remove it.
            var doc = await _dbContext.Documents
                .Where(d => d.Id == documentId)
                .Select(d => new { d.Id, d.Name, d.TenantId, d.ParentId, d.CreatedAt, d.CreatedByUserId, d.CreatedByServiceAccountId, d.MaskVersionId, SensitivityLabelName = d.SensitivityLabelId == null ? null : _dbContext.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Name).FirstOrDefault(), SensitivityLabelRank = d.SensitivityLabelId == null ? (int?)null : _dbContext.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => (int?)l.Rank).FirstOrDefault() })
                .SingleOrDefaultAsync(cancellationToken);

            if (doc is null)
            {
                await RemoveAsync(documentId, cancellationToken);
                return true;
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

            var version = await _dbContext.DocumentVersions
                .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => new { v.Id, v.ObjectKey, v.CreatedAt, v.CreatedByUserId, v.CreatedByServiceAccountId, v.DocumentDate })
                .FirstOrDefaultAsync(cancellationToken);

            var repositoryId = await ResolveRootAsync(documentId, cancellationToken);
            var allowedPrincipals = await _rightsCalculator.GetVisibilityPrincipalsAsync(documentId, cancellationToken);

            // Free-form tags (ADR "Document tags") — indexed as a keyword array for the Tags facet + system[tag] filter.
            var tags = await _dbContext.DocumentTags.Where(t => t.DocumentId == documentId).Select(t => t.Tag).ToListAsync(cancellationToken);

            // System fields (ADR "System-field search"): creator names resolved from ids, plus document /
            // version dates and the issuing date. Version fields are null for folders (no version).
            var createdBy = await ResolveCreatorNameAsync(doc.CreatedByUserId, doc.CreatedByServiceAccountId, cancellationToken);
            var versionCreatedBy = version is null ? null
                : await ResolveCreatorNameAsync(version.CreatedByUserId, version.CreatedByServiceAccountId, cancellationToken);

            var content = "";
            if (version is not null)
            {
                var existing = await GetIndexedAsync(documentId, cancellationToken);
                if (existing is { IndexedVersionId: { } indexed } && indexed == version.Id)
                {
                    content = existing.Content;
                }
                else if (ArchiveContentExtractor.IsZip(version.ObjectKey))
                {
                    // A .zip isn't unpacked (ADR "Zip file browsing"), but its first-level entries' names and
                    // extracted text are indexed so a search finds the zip that contains a file.
                    content = await ArchiveContentExtractor.ExtractAsync(_storage, _archiveReader, _extractor, version.ObjectKey, cancellationToken);
                }
                else
                {
                    await using var stream = await _storage.GetObjectAsync(version.ObjectKey, cancellationToken);
                    content = await _extractor.ExtractAsync(stream, "application/octet-stream", cancellationToken);
                }
            }

            var body = JsonSerializer.Serialize(new
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
                // Sensitivity label (ADR "Data classification / sensitivity labels") — the name as a keyword, null
                // when unclassified, for the system[sensitivityLabel] filter.
                sensitivityLabel = doc.SensitivityLabelName,
                // Numeric label rank for the clearance-ceiling filter (ADR "Sensitivity clearance enforcement");
                // unlabelled indexes as 0 so it always passes a rank<=clearance filter.
                sensitivityRank = doc.SensitivityLabelRank ?? 0,
                tags,
                // Facet dimensions (ADR "Search facet refinements") — null for folders (no version).
                fileType = version is null ? null : SearchFieldMapper.FileType(version.ObjectKey),
                documentYear = version is null ? null : version.DocumentDate.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

            _logger.LogDebug("Indexing document {Document} to the search index.", documentId);
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{Alias}/_doc/{documentId}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Indexing document {Document} returned {Status}.", documentId, response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Indexing document {Document} failed.", documentId);
            return false;
        }
    }

    public async Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Alias}/_doc/{documentId}");
            using var response = await _http.SendAsync(request, cancellationToken); // 404 is fine
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Removing document {Document} from the index failed.", documentId);
        }
    }

    private sealed record Indexed(string Content, Guid? IndexedVersionId);

    private async Task<Indexed?> GetIndexedAsync(Guid documentId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"{Alias}/_doc/{documentId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("_source", out var source))
        {
            return null;
        }

        var content = source.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        Guid? version = source.TryGetProperty("indexedVersionId", out var v)
            && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;
        return new Indexed(content, version);
    }

    // Resolves a creator id to its display name for indexing (ADR "System-field search") — User.DisplayName
    // or ServiceAccount.Name. The name is what's searched (the DB keeps the id); it can go stale on a rename
    // until the next reindex.
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

    // The alias is created by the rebuild (blue-green). Until it exists, per-doc writes are skipped so they
    // don't auto-create a plain "documents" index. Caches only the positive result (once ready, always ready).
    private async Task<bool> AliasReadyAsync(CancellationToken cancellationToken)
    {
        if (_aliasReady)
        {
            return true;
        }

        try
        {
            using var response = await _http.GetAsync($"_alias/{Alias}", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _aliasReady = true;
                return true;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Checking the OpenSearch alias failed.");
        }

        return false;
    }
}

// No-op indexer — registered when OpenSearch isn't configured (nothing drains the queue either).
public sealed class NullDocumentIndexer : IDocumentIndexer
{
    public Task<bool> SyncAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task RemoveAsync(Guid documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
