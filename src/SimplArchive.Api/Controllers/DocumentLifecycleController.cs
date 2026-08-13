using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Ocr;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The document's lifecycle transitions: check-out/check-in (the exclusive edit lock), soft delete to the
/// recycle bin, restore, and permanent purge. Split out of DocumentsController (#466); routes unchanged.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class DocumentLifecycleController : ControllerBase
{
    private readonly IObjectStorageClient _objectStorage;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;
    private readonly ILegalHoldService _legalHold;
    private readonly Documents.DocumentPurger _purger;
    private readonly Documents.DocumentRestorer _restorer;
    private readonly IDocumentIndexQueue _queue;
    private readonly IWormLockService _wormLock;

    public DocumentLifecycleController(
        IObjectStorageClient objectStorage,
        IUserSystemRightsResolver userSystemRights,
        ICurrentUserAccessor currentUserAccessor,
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        IAuditRecorder audit,
        ILegalHoldService legalHold,
        Documents.DocumentPurger purger,
        Documents.DocumentRestorer restorer,
        IDocumentIndexQueue queue,
        IWormLockService wormLock)
    {
        _objectStorage = objectStorage;
        _userSystemRights = userSystemRights;
        _currentUserAccessor = currentUserAccessor;
        _dbContext = dbContext;
        _access = access;
        _audit = audit;
        _legalHold = legalHold;
        _purger = purger;
        _restorer = restorer;
        _queue = queue;
        _wormLock = wormLock;
    }

    // Check-out (acquire the exclusive edit lock) — ADR "Document check-out / check-in". User-only (a
    // ServiceAccount can't hold a lock); requires CanEditContent + at least one confirmed version (there must
    // be something to edit); 409 if already held by someone else; idempotent no-op if already held by the
    // caller. No If-Match — this is a lock action, not a content edit (like POST restore).
    [HttpPut("checkout")]
    public async Task<IActionResult> CheckOut(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid(); // only an interactive User can check a document out
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        if (document.CheckedOutByUserId is { } holder && holder != userId)
        {
            throw new DocumentAlreadyCheckedOutException();
        }

        if (!await _dbContext.DocumentVersions.AnyAsync(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed, cancellationToken))
        {
            throw new NothingToCheckOutException();
        }

        if (document.CheckedOutByUserId is null)
        {
            document.CheckedOutByUserId = userId;
            document.CheckedOutAt = DateTimeOffset.UtcNow;
            document.CheckoutReminderSentAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync(AuditActions.DocumentCheckedOut, "Document", documentId, document.Name, cancellationToken: cancellationToken);
        }

        SetETag(document.ConcurrencyToken);
        return Ok(new DocumentsController.DocumentResource { Id = documentId, Name = document.Name });
    }

    // Check-in / unlock / discard / override (release the lock) — ADR "Document check-out / check-in". The
    // holder releases their own lock; a CanOverrideCheckout holder can force-release someone else's ("break
    // the lock"). The new version, if any, was uploaded through the normal versions flow while the lock was
    // held — release does not itself create a version. Idempotent when not checked out.
    [HttpDelete("checkout")]
    public async Task<IActionResult> CheckIn(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (document.CheckedOutByUserId is not { } holder)
        {
            return NoContent(); // not checked out — nothing to release
        }

        var isOverride = holder != userId;
        if (isOverride)
        {
            var rights = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            if (!rights.CanOverrideCheckout)
            {
                return Forbid(); // only the holder or a CanOverrideCheckout holder may release
            }
        }

        var holderName = await _dbContext.Users.Where(u => u.Id == holder).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken);
        document.CheckedOutByUserId = null;
        document.CheckedOutAt = null;
        document.CheckoutReminderSentAt = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Releasing ends the check-out, so the holder's cloud working-copy stash is no longer needed (ADR
        // "Check-out working-copy stash + exit guard") — remove it best-effort. Keyed by the HOLDER's user id
        // (an override releases someone else's lock, so their stash is what's cleared).
        try
        {
            await _objectStorage.DeleteObjectAsync(CheckoutsController.StashKey(document.TenantId, holder, documentId), cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort — an orphaned stash object is harmless.
        }

        await _audit.RecordAsync(
            isOverride ? AuditActions.DocumentCheckoutOverridden : AuditActions.DocumentCheckedIn,
            "Document", documentId, document.Name,
            isOverride ? $"released the check-out held by {holderName}" : null,
            cancellationToken: cancellationToken);

        SetETag(document.ConcurrencyToken);
        return NoContent();
    }

    // Soft delete (sets DeletedAt, ADR "Document recycle-bin data shape") — cascades recursively to every
    // descendant in the same operation, so the whole subtree moves to the recycle bin together (ADR
    // "Document delete/restore (recycle bin) implementation"). Requires If-Match like PUT, consistent with
    // ADR 0003's "ETag/If-Match on every mutation" commitment.
    [HttpDelete]
    public async Task<IActionResult> Delete(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanDelete)
        {
            return Forbid();
        }

        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !TryParseETag(ifMatchValues.ToString(), out var ifMatchToken))
        {
            throw new IfMatchRequiredException();
        }

        var toDelete = await _dbContext.CollectSubtreeAsync(documentId, document, cancellationToken);

        // A legal hold freezes deletion: refuse if the target is under an ancestor hold, or any document in the
        // subtree being cascade-deleted is itself directly held (ADR "Legal hold & retention enforcement").
        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken)
            || await _legalHold.AnyDirectlyHeldAsync(toDelete.Select(d => d.Id).ToList(), cancellationToken))
        {
            throw DocumentUnderLegalHoldException.ForDeletion();
        }

        // The full edit-lock blocks deletion too: refuse if the target or any document in the cascade is
        // checked out by a DIFFERENT user (ADR "Document check-out / check-in").
        if (toDelete.Any(d => d.CheckedOutByUserId is { } h && h != _currentUserAccessor.UserId))
        {
            throw DocumentCheckedOutException.ForDeletion();
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var doc in toDelete)
        {
            doc.DeletedAt = now;
        }

        _dbContext.Entry(document).Property(d => d.ConcurrencyToken).OriginalValue = ifMatchToken;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForDocument();
        }

        foreach (var doc in toDelete)
        {
            await _queue.EnqueueAsync(doc.Id, cancellationToken);
        }

        await _audit.RecordAsync(AuditActions.DocumentDeleted, "Document", documentId, document.Name,
            toDelete.Count > 1 ? $"cascade: {toDelete.Count} items" : null, cancellationToken: cancellationToken);

        return NoContent();
    }

    // Restores a soft-deleted document (and, recursively, every descendant that was cascade-deleted with
    // it) — the natural inverse of DELETE, reusing the same CanDelete right rather than a new one (ADR
    // "Recycle bin restore workflow"). Only the top-level target may need reparenting to a "Recovered
    // Items" folder: cascaded descendants' ParentId always points within the subtree being restored
    // together, so it's already valid by construction. Idempotent: restoring an already-active document is
    // a no-op that returns its current state.
    [HttpPost("restore")]
    public async Task<IActionResult> Restore(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .IgnoreQueryFilters(["SoftDeleteFilter"])
            .SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);

        if (document is null)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);

        if (!rights.CanDelete)
        {
            return Forbid();
        }

        // The restore mechanics (reparent-to-Recovered-Items if the original parent is gone, un-delete the whole
        // subtree, re-index) live in the shared DocumentRestorer (ADR "Bulk restore from the recycle bin");
        // restoring an already-active document is an idempotent no-op (Restored == false → no audit).
        var (userId, serviceAccountId) = _access.GetCallerIdentity();
        if (await _restorer.RestoreAsync(document, userId, serviceAccountId, cancellationToken))
        {
            await _audit.RecordAsync(AuditActions.DocumentRestored, "Document", documentId, document.Name, cancellationToken: cancellationToken);
        }

        return Ok(new DocumentsController.DocumentResource
        {
            Id = documentId,
            Name = document.Name,
            Links = [new Link("self", Url.Action(nameof(DocumentsController.Get), "Documents", new { documentId })!, "GET")],
        });
    }

    // Permanently removes a recycle-bin document + its soft-deleted subtree (blobs + rows + search index),
    // irreversibly. Tenant-admin-only; refused for an active (not-recycled) document or one under legal hold.
    // A destructive action sub-resource (POST), like restore — not the soft-delete DELETE. See ADR "Manual
    // hard-delete / purge".
    [HttpPost("purge")]
    public async Task<IActionResult> Purge(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await _access.IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var subtree = await _purger.CollectSubtreeAsync(documentId, cancellationToken);
        if (subtree is null)
        {
            return NotFound();
        }

        if (subtree[0].DeletedAt is null)
        {
            throw new CannotPurgeActiveException();
        }

        var ids = subtree.Select(d => d.Id).ToList();
        if (await _purger.AnyHeldAsync(ids, cancellationToken))
        {
            throw DocumentUnderLegalHoldException.ForPurge();
        }

        var purged = await _purger.PurgeAsync(subtree, cancellationToken);
        foreach (var (id, name) in purged)
        {
            await _audit.RecordAsync(AuditActions.DocumentPurged, "Document", id, name, cancellationToken: cancellationToken);
        }

        return NoContent();
    }

    private void SetETag(Guid concurrencyToken)
    {
        Response.Headers.ETag = $"\"{concurrencyToken}\"";
    }

    private static bool TryParseETag(string headerValue, out Guid token)
    {
        return Guid.TryParse(headerValue.Trim('"'), out token);
    }
}
