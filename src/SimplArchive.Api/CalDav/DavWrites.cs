using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;

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
    internal static async Task<IActionResult> PutAsync(
        DavControllerContext context, IServiceProvider services, Guid folderId, string resourceName)
    {
        var (db, rights, protocol) = (context.Db, context.Rights, context.Protocol);
        var folder = await db.Documents.FirstOrDefaultAsync(d => d.Id == folderId, context.Cancellation);
        if (folder is null)
        {
            return new NotFoundResult();
        }

        var existing = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.Cancellation);
        var folderRights = await rights.GetEffectiveRightsAsync(context.UserId, folderId);

        // Creating needs CanCreateSubItems on the collection; replacing needs CanEditContent on the item.
        if (existing is null)
        {
            if (folder.PersonalOfUserId != context.UserId && !folderRights.CanCreateSubItems)
            {
                return new ForbidResult(Authentication.DavAuthenticationDefaults.Scheme);
            }
        }
        else if (!(await rights.GetEffectiveRightsAsync(context.UserId, existing.DocumentId)).CanEditContent)
        {
            return new ForbidResult(Authentication.DavAuthenticationDefaults.Scheme);
        }

        if (PreconditionFailed(context, existing?.ETag))
        {
            return new StatusCodeResult(StatusCodes.Status412PreconditionFailed);
        }

        await using var buffered = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffered, context.Cancellation);
        buffered.Position = 0;
        if (buffered.Length == 0)
        {
            return new BadRequestResult();
        }

        if (!await services.GetRequiredService<IStorageQuotaService>().CanStoreAsync(context.TenantId, buffered.Length, context.Cancellation))
        {
            return new StatusCodeResult(StatusCodes.Status507InsufficientStorage);
        }

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();

        // The object key groups by document (ADR 0530): an existing item reuses its filing year + storage folder,
        // a new one starts its own.
        Document document;
        if (existing is not null)
        {
            document = await db.Documents.FirstAsync(d => d.Id == existing.DocumentId, context.Cancellation);
        }
        else
        {
            // Named after the resource for now; classification renames it to the item's real title (SUMMARY /
            // FN) once the bytes are read, which is also what fills the UID field the name is derived from.
            document = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = context.TenantId,
                ParentId = folderId,
                Name = Path.GetFileNameWithoutExtension(resourceName),
                CreatedByUserId = context.UserId,
                CreatedAt = now,
                StorageFolderId = Guid.NewGuid(),
            };
            db.Documents.Add(document);
            try
            {
                await db.SaveChangesAsync(context.Cancellation);
            }
            catch (InvalidOperationException)
            {
                return new ConflictResult(); // sibling-name clash
            }
        }

        var objectKey = ObjectKeyBuilder.Build(context.TenantId, document.CreatedAt, document.StorageFolderId, versionId, protocol.Extension);
        await services.GetRequiredService<IObjectStorageClient>()
            .PutObjectAsync(objectKey, buffered, protocol.ContentType, context.Cancellation);

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = context.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = context.UserId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        db.DocumentVersions.Add(version);
        await db.SaveChangesAsync(context.Cancellation);

        // The finalizer confirms the version, classifies the content into the Contact/Calendar mask and fills
        // its fields — the same path every other upload takes.
        await services.GetRequiredService<DocumentFinalizer>().FinalizeAsync(version, context.Cancellation);

        await services.GetRequiredService<IAuditRecorder>().RecordAsync(
            existing is null ? AuditActions.DocumentFiled : AuditActions.DocumentVersionAdded,
            "Document", document.Id, document.Name,
            existing is null ? $"Filed over {protocol.NamespacePrefix}DAV" : $"New version over {protocol.NamespacePrefix}DAV",
            cancellationToken: context.Cancellation);

        // Re-read the document for the ETag: the finalizer's save regenerated the concurrency token.
        var stored = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.Cancellation);
        if (stored is not null)
        {
            context.Response.Headers.ETag = $"\"{stored.ETag}\"";
        }

        return new StatusCodeResult(existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent);
    }

    internal static async Task<IActionResult> DeleteAsync(
        DavControllerContext context, IServiceProvider services, Guid folderId, string resourceName)
    {
        var (db, rights, protocol) = (context.Db, context.Rights, context.Protocol);
        var item = await DavTree.ItemAsync(db, protocol, folderId, resourceName, context.Cancellation);
        if (item is null)
        {
            return new NotFoundResult();
        }

        if (!(await rights.GetEffectiveRightsAsync(context.UserId, item.DocumentId)).CanDelete)
        {
            return new ForbidResult(Authentication.DavAuthenticationDefaults.Scheme);
        }

        if (PreconditionFailed(context, item.ETag))
        {
            return new StatusCodeResult(StatusCodes.Status412PreconditionFailed);
        }

        // A document under a legal hold cannot be deleted by any door (ADR 0326) — the DAV client is told so
        // rather than silently succeeding.
        var held = await db.LegalHoldItems.AnyAsync(
            i => i.DocumentId == item.DocumentId && db.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null),
            context.Cancellation);
        if (held)
        {
            return new ForbidResult(Authentication.DavAuthenticationDefaults.Scheme);
        }

        var document = await db.Documents.FirstAsync(d => d.Id == item.DocumentId, context.Cancellation);
        document.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(context.Cancellation);

        await services.GetRequiredService<IAuditRecorder>().RecordAsync(
            AuditActions.DocumentDeleted, "Document", document.Id, document.Name,
            $"Deleted over {protocol.NamespacePrefix}DAV", cancellationToken: context.Cancellation);

        return new NoContentResult();
    }

    /// <summary>
    /// The conditional-request rules a DAV client relies on (RFC 7232, and how CalDAV clients avoid clobbering
    /// each other). Learned from the sister project's `PreconditionFailed`, which covers a case improvising
    /// against the RFC alone had missed: <c>If-None-Match: *</c> is how a client says "create this, but only if
    /// nothing is there" — without it, a first-write race silently overwrites the winner.
    /// </summary>
    /// <param name="currentETag">The item's current tag, or null when nothing is stored at this address.</param>
    private static bool PreconditionFailed(DavControllerContext context, string? currentETag)
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
