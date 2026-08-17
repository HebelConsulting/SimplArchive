using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.CalDav;

/// <summary>
/// CalDAV/CardDAV writes (#564 slice 2, ADR 0620): a <c>PUT</c> stores the item and makes a NEW VERSION of the
/// matching document (creating one when nothing matches), a <c>DELETE</c> soft-deletes it into the recycle bin.
/// One implementation for both protocols, driven by <see cref="DavProtocol"/> like the read side.
/// </summary>
/// <remarks>
/// The item's identity is its UID, and the resource name is derived from it — but a client picks the resource
/// name itself on PUT, so the name is treated as the address and the stored content decides what the item IS:
/// classification reads the bytes and fills the mask fields, exactly as it does for an upload through any other
/// door. That is what keeps a contact created here indistinguishable from one dragged into the workbench.
/// </remarks>
internal static class DavWrites
{
    internal static async Task PutAsync(
        HttpContext context, SimplArchiveDbContext db, IEffectiveRightsCalculator rights, IServiceProvider services,
        User user, DavProtocol protocol, Guid folderId, string resourceName)
    {
        var folder = await db.Documents.FirstOrDefaultAsync(d => d.Id == folderId, context.RequestAborted);
        if (folder is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var existing = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.RequestAborted);
        var folderRights = await rights.GetEffectiveRightsAsync(user.Id, folderId);

        // Creating needs CanCreateSubItems on the collection; replacing needs CanEditContent on the item.
        if (existing is null)
        {
            if (folder.PersonalOfUserId != user.Id && !folderRights.CanCreateSubItems)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (!(await rights.GetEffectiveRightsAsync(user.Id, existing.DocumentId)).CanEditContent)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (PreconditionFailed(context, existing?.ETag))
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.RequestAborted);
        buffered.Position = 0;
        if (buffered.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!await services.GetRequiredService<IStorageQuotaService>().CanStoreAsync(user.TenantId, buffered.Length, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();

        // The object key groups by document (ADR 0530): an existing item reuses its filing year + storage folder,
        // a new one starts its own.
        Document document;
        if (existing is not null)
        {
            document = await db.Documents.FirstAsync(d => d.Id == existing.DocumentId, context.RequestAborted);
        }
        else
        {
            // Named after the resource for now; classification renames it to the item's real title (SUMMARY /
            // FN) once the bytes are read, which is also what fills the UID field the name is derived from.
            document = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = user.TenantId,
                ParentId = folderId,
                Name = Path.GetFileNameWithoutExtension(resourceName),
                CreatedByUserId = user.Id,
                CreatedAt = now,
                StorageFolderId = Guid.NewGuid(),
            };
            db.Documents.Add(document);
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict; // sibling-name clash
                return;
            }
        }

        var objectKey = ObjectKeyBuilder.Build(user.TenantId, document.CreatedAt, document.StorageFolderId, versionId, protocol.Extension);
        await services.GetRequiredService<IObjectStorageClient>()
            .PutObjectAsync(objectKey, buffered, protocol.ContentType, context.RequestAborted);

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = user.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = user.Id,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        db.DocumentVersions.Add(version);
        await db.SaveChangesAsync(context.RequestAborted);

        // The finalizer confirms the version, classifies the content into the Contact/Calendar mask and fills
        // its fields — the same path every other upload takes.
        await services.GetRequiredService<DocumentFinalizer>().FinalizeAsync(version, context.RequestAborted);

        await services.GetRequiredService<IAuditRecorder>().RecordAsync(
            existing is null ? AuditActions.DocumentFiled : AuditActions.DocumentVersionAdded,
            "Document", document.Id, document.Name,
            existing is null ? $"Filed over {protocol.NamespacePrefix}DAV" : $"New version over {protocol.NamespacePrefix}DAV",
            cancellationToken: context.RequestAborted);

        // Re-read the document for the ETag: the finalizer's save regenerated the concurrency token.
        var stored = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.RequestAborted);
        if (stored is not null)
        {
            context.Response.Headers.ETag = $"\"{stored.ETag}\"";
        }

        context.Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
    }

    internal static async Task DeleteAsync(
        HttpContext context, SimplArchiveDbContext db, IEffectiveRightsCalculator rights, IServiceProvider services,
        User user, DavProtocol protocol, Guid folderId, string resourceName)
    {
        var item = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.RequestAborted);
        if (item is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!(await rights.GetEffectiveRightsAsync(user.Id, item.DocumentId)).CanDelete)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (PreconditionFailed(context, item.ETag))
        {
            context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return;
        }

        // A document under a legal hold cannot be deleted by any door (ADR 0326) — the DAV client is told so
        // rather than silently succeeding.
        var held = await db.LegalHoldItems.AnyAsync(
            i => i.DocumentId == item.DocumentId && db.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null),
            context.RequestAborted);
        if (held)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var document = await db.Documents.FirstAsync(d => d.Id == item.DocumentId, context.RequestAborted);
        document.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.RequestAborted);

        await services.GetRequiredService<IAuditRecorder>().RecordAsync(
            AuditActions.DocumentDeleted, "Document", document.Id, document.Name,
            $"Deleted over {protocol.NamespacePrefix}DAV", cancellationToken: context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// The conditional-request rules a DAV client relies on (RFC 7232, and how CalDAV clients avoid clobbering
    /// each other). Learned from the sister project's `PreconditionFailed`, which covers a case improvising
    /// against the RFC alone had missed: <c>If-None-Match: *</c> is how a client says "create this, but only if
    /// nothing is there" — without it, a first-write race silently overwrites the winner.
    /// </summary>
    /// <param name="currentETag">The item's current tag, or null when nothing is stored at this address.</param>
    private static bool PreconditionFailed(HttpContext context, string? currentETag)
    {
        // "Only if absent" — the client is creating, not replacing.
        if (context.Request.Headers.IfNoneMatch.ToString().Trim() == "*" && currentETag is not null)
        {
            return true;
        }

        var ifMatch = context.Request.Headers.IfMatch.ToString().Trim();
        if (ifMatch.Length == 0)
        {
            return false; // no precondition — clients routinely omit it on a first write
        }

        // "*" means "if anything is here", so it fails only when nothing is.
        if (ifMatch == "*")
        {
            return currentETag is null;
        }

        // A tag arrives quoted, possibly weak, possibly as a list — a match against any entry is a match.
        return !ifMatch.Split(',')
            .Select(tag => tag.Trim().TrimStart('W', '/').Trim().Trim('"'))
            .Any(tag => tag == currentETag);
    }
}
