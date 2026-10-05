using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Manuals;

/// <summary>What one filing check did (ADR 0891).</summary>
public enum ManualFilingOutcome
{
    /// <summary>There is no manual to file — the module declares none, or the core's is not in this build.</summary>
    NotDeclared,

    /// <summary>The module is not activated for the current tenant, so nothing is filed for it.</summary>
    NotActivated,

    /// <summary>There is nowhere to file it — the tenant has no standard repository; logged as a Warning.</summary>
    NoLocation,

    /// <summary>The archived manual already carries these bytes.</summary>
    Unchanged,

    /// <summary>A new manual document was created.</summary>
    Filed,

    /// <summary>Different bytes arrived; they became a new version of the same document.</summary>
    Versioned,

    /// <summary>The archive refused the filing; logged as a Warning.</summary>
    Refused,
}

/// <summary>
/// One manual to file: the bytes, what the document is called, and the four Manual fields the core fills (ADR 0891).
/// </summary>
public sealed record ManualSource(
    string DocumentName,
    string FileName,
    string ContentType,
    Func<Stream> Open,
    string ProductName,
    string ArticleNumber,
    string ProductVersion);

/// <summary>
/// Files a manual into the tenant's manuals folder and keeps it current — SimplArchive's own and every module's
/// (ADR 0891), by one implementation.
/// </summary>
/// <remarks>
/// <para>
/// One document per manual: created when there is none (or it was deleted), given a NEW VERSION when the bytes
/// differ from its current version, left alone when they match, and its Manual fields refreshed with every version.
/// What differs between the core's manual and a module's — where the document's id is remembered, and who is named as
/// its author — is passed in as lambdas by the two entry points below, so the filing itself exists once.
/// </para>
/// <para>
/// <b>Safe across two instances</b>: every check runs in one transaction that FIRST locks the tenant's row (a no-op
/// UPDATE) and only then reads what is filed. A sibling instance running the same check waits on that lock and then
/// reads the committed document and hash — the same bytes — and does nothing. Read-then-lock would let both see
/// "no manual" and file two (ADR 0836's lesson, applied to a startup path).
/// </para>
/// <para>
/// <b>A refusal costs the manual, never the act that triggered it</b> — provisioning, activation and startup all
/// proceed, and a Warning says what to fix.
/// </para>
/// </remarks>
public sealed class ManualFiler(
    SimplArchiveDbContext dbContext,
    IObjectStorageClient storage,
    DocumentFinalizer finalizer,
    IAuditRecorder audit,
    IWebHostEnvironment environment,
    ICurrentTenantAccessor tenant,
    ILogger<ManualFiler> logger)
{
    /// <summary>The Manual "Article number" that identifies SimplArchive's own manual — and how it is found again.</summary>
    public const string CoreArticleNumber = "simplarchive-core";

    /// <summary>Where the served core manual lives under wwwroot — the same file the download link serves.</summary>
    public const string CoreManualPath = "download/manual/SimplArchive-Manual.pdf";

    /// <summary>Files SimplArchive's own user manual in the current tenant.</summary>
    /// <remarks>
    /// Its document is found again by its Manual fields (Article number = <see cref="CoreArticleNumber"/>) rather than
    /// by a remembered id, which needs no column: the core files exactly one such document per tenant, and a person
    /// filing a document that claims to be it would only get it versioned here.
    /// </remarks>
    public Task<ManualFilingOutcome> FileCoreManualAsync(CancellationToken cancellationToken = default)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(CoreManualPath);
        if (!file.Exists)
        {
            logger.LogDebug("No core manual at {Path} in this build; nothing to file.", CoreManualPath);
            return Task.FromResult(ManualFilingOutcome.NotDeclared);
        }

        var source = new ManualSource("SimplArchive Manual", Path.GetFileName(CoreManualPath), "application/pdf",
            file.CreateReadStream, "SimplArchive", CoreArticleNumber, ServerBuildInfo.Version);

        return FileAsync(source,
            findExisting: () => CoreManualDocumentIdAsync(cancellationToken),
            remember: _ => Task.CompletedTask,
            author: AuthorOfStandardRepositoryAsync,
            auditActor: _ => (AuditActorType.System, Guid.Empty, "SimplArchive"),
            cancellationToken);
    }

    /// <summary>Files the manual <paramref name="module"/> ships in its package, in the current tenant.</summary>
    public async Task<ManualFilingOutcome> FileModuleManualAsync(IIndustryModule module, CancellationToken cancellationToken = default)
    {
        if (module.Manual is not { } manual)
        {
            return ManualFilingOutcome.NotDeclared;
        }

        if (!await dbContext.ModuleActivations.AnyAsync(a => a.ModuleId == module.ModuleId, cancellationToken))
        {
            return ManualFilingOutcome.NotActivated;
        }

        var version = module.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? module.GetType().Assembly.GetName().Version?.ToString() ?? string.Empty;
        var source = new ManualSource($"{module.DisplayName} Manual", manual.FileName, manual.ContentType, manual.Open,
            module.DisplayName, module.ModuleId, version);

        var principalId = Guid.Empty;
        return await FileAsync(source,
            findExisting: () => dbContext.ModuleActivations
                .Where(a => a.ModuleId == module.ModuleId)
                .Select(a => a.ManualDocumentId)
                .SingleAsync(cancellationToken),
            remember: async documentId =>
            {
                var activation = await dbContext.ModuleActivations.SingleAsync(a => a.ModuleId == module.ModuleId, cancellationToken);
                activation.ManualDocumentId = documentId;
            },
            author: async () =>
            {
                var principal = await ModulePrincipal.EnsureAsync(dbContext, module.ModuleId, module.DisplayName, tenant.TenantId!.Value, cancellationToken);
                principalId = principal.Id;
                return (null, principal.Id);
            },
            auditActor: name => (AuditActorType.ServiceAccount, principalId, $"Module: {module.DisplayName}"),
            cancellationToken);
    }

    private async Task<ManualFilingOutcome> FileAsync(
        ManualSource source,
        Func<Task<Guid?>> findExisting,
        Func<Guid, Task> remember,
        Func<Task<(Guid? UserId, Guid? ServiceAccountId)>> author,
        Func<string, (AuditActorType Type, Guid Id, string Name)> auditActor,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Filing a manual needs a current tenant.");
        byte[] bytes;
        await using (var stream = source.Open())
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var owned = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await using var transaction = owned;

        // THE LOCK, and it must come before any read: a sibling instance running the same check waits here and then
        // reads what this one committed. TOKEN DELIBERATELY NOT MOVED: nothing changes — the statement exists only
        // for the row lock it takes.
        await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.StandardRepositoryId, t => t.StandardRepositoryId), cancellationToken);

        // Soft-deleted reads as gone (the default filter): a manual someone put in the recycle bin is filed afresh.
        var document = await findExisting() is { } existingId
            ? await dbContext.Documents.SingleOrDefaultAsync(d => d.Id == existingId, cancellationToken)
            : null;

        ManualFilingOutcome outcome;
        try
        {
            if (document is not null)
            {
                var current = await CurrentVersion.ResolveAsync(
                    dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
                if (string.Equals(current?.Sha256Hash, hash, StringComparison.Ordinal))
                {
                    return ManualFilingOutcome.Unchanged;
                }

                outcome = ManualFilingOutcome.Versioned;
            }
            else
            {
                if (await ManualsFolder.FindOrCreateAsync(dbContext, tenantId, logger, cancellationToken) is not { } folder)
                {
                    return ManualFilingOutcome.NoLocation;
                }

                document = await CreateDocumentAsync(source, tenantId, folder.Id, await author(), cancellationToken);
                await remember(document.Id);
                outcome = ManualFilingOutcome.Filed;
            }

            await AddVersionAsync(source, document, bytes, await author(), cancellationToken);
            await SetFieldsAsync(document, source, cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception refusal) when (refusal is InvalidOperationException or ApiException)
        {
            // The archive's own invariants refused it — a typed folder that admits no plain entry, a same-named
            // sibling. Rolled back by disposal (when the transaction is ours); the triggering act carries on.
            dbContext.ChangeTracker.Clear();
            logger.LogWarning(refusal,
                "The manual '{Name}' could not be filed for tenant {TenantId} ({Reason}). The next start tries again.",
                source.DocumentName, tenantId, refusal.Message);
            return ManualFilingOutcome.Refused;
        }

        // After the commit, never before: an event announcing a filing that then rolled back is a lie.
        var actor = auditActor(source.DocumentName);
        await audit.RecordForActorAsync(actor.Type, actor.Id, actor.Name, tenantId, AuditActions.ManualFiled,
            "Document", document.Id, document.Name,
            $"{(outcome == ManualFilingOutcome.Filed ? "Filed" : "New version")} from {source.FileName} version "
            + $"{source.ProductVersion} (SHA-256 {hash})",
            cancellationToken);
        logger.LogInformation("Manual {Name}: {Outcome} as document {DocumentId} for tenant {TenantId}",
            source.DocumentName, outcome, document.Id, tenantId);
        return outcome;
    }

    private async Task<Guid?> CoreManualDocumentIdAsync(CancellationToken cancellationToken)
    {
        var articleNumber = WellKnownMaskIds.ManualFields.ArticleNumber;
        return await dbContext.FieldValues
            .Where(v => v.Value == CoreArticleNumber
                && dbContext.FieldDefinitions.Any(f => f.Id == v.FieldDefinitionId && f.Name == articleNumber
                    && dbContext.MaskVersions.Any(mv => mv.Id == f.MaskVersionId && mv.MaskId == WellKnownMaskIds.Manual))
                && dbContext.Documents.Any(d => d.Id == v.DocumentId))
            .OrderBy(v => v.DocumentId)
            .Select(v => (Guid?)v.DocumentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<(Guid? UserId, Guid? ServiceAccountId)> AuthorOfStandardRepositoryAsync()
    {
        var standard = await dbContext.Tenants
            .Where(t => t.Id == tenant.TenantId)
            .Join(dbContext.Documents, t => t.StandardRepositoryId, d => (Guid?)d.Id, (t, d) => new { d.CreatedByUserId, d.CreatedByServiceAccountId })
            .SingleOrDefaultAsync();
        return standard is null ? (null, null) : (standard.CreatedByUserId, standard.CreatedByUserId is null ? standard.CreatedByServiceAccountId : null);
    }

    private async Task<Document> CreateDocumentAsync(
        ManualSource source, Guid tenantId, Guid folderId, (Guid? UserId, Guid? ServiceAccountId) by, CancellationToken cancellationToken)
    {
        var manualMask = await dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Manual && v.IsCurrent)
            .Select(v => v.Id)
            .SingleAsync(cancellationToken);

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = folderId,
            Name = source.DocumentName,
            MaskVersionId = manualMask,
            CreatedByUserId = by.UserId,
            CreatedByServiceAccountId = by.ServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);
        return document;
    }

    private async Task AddVersionAsync(
        ManualSource source, Document document, byte[] bytes, (Guid? UserId, Guid? ServiceAccountId) by, CancellationToken cancellationToken)
    {
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(
            document.TenantId, document.CreatedAt, document.StorageFolderId, versionId, Path.GetExtension(source.FileName));

        // Through the seam, so at-rest encryption applies exactly as it does to an upload.
        using (var content = new MemoryStream(bytes, writable: false))
        {
            await storage.PutObjectAsync(objectKey, content, source.ContentType, cancellationToken);
        }

        // PENDING and confirmed by the finalizer, never a hand-written Confirmed row: it hashes the stored bytes itself,
        // numbers the version, and joins our transaction. The document already wears the Manual mask, which the
        // finalizer keeps (it classifies only generic masks).
        await finalizer.FileAsync(new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = by.UserId,
            CreatedByServiceAccountId = by.ServiceAccountId,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow,
            Comment = $"{source.FileName} {source.ProductVersion}".Trim(),
        }, cancellationToken);

        // A pinned current version would hide the new one (CurrentVersion.ResolveAsync honours the pin).
        if (document.CurrentVersionId is not null)
        {
            document.CurrentVersionId = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>The four Manual fields the core owns for its own manuals, refreshed with every version.</summary>
    private async Task SetFieldsAsync(Document document, ManualSource source, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<string, string>
        {
            [WellKnownMaskIds.ManualFields.Brand] = "SimplArchive",
            [WellKnownMaskIds.ManualFields.ProductName] = source.ProductName,
            [WellKnownMaskIds.ManualFields.ArticleNumber] = source.ArticleNumber,
            [WellKnownMaskIds.ManualFields.ProductVersion] = source.ProductVersion,
        };

        var definitions = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == document.MaskVersionId && wanted.Keys.Contains(f.Name))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        var existing = await dbContext.FieldValues
            .Where(v => v.DocumentId == document.Id)
            .ToListAsync(cancellationToken);

        foreach (var definition in definitions)
        {
            var value = wanted[definition.Name];
            var row = existing.FirstOrDefault(v => v.FieldDefinitionId == definition.Id);
            if (string.IsNullOrEmpty(value))
            {
                if (row is not null)
                {
                    dbContext.FieldValues.Remove(row);
                }
            }
            else if (row is null)
            {
                dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = document.TenantId,
                    DocumentId = document.Id,
                    FieldDefinitionId = definition.Id,
                    Value = value,
                });
            }
            else
            {
                row.Value = value;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
