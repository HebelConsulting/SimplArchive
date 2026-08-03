using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's currently checked-out documents (ADR "Document check-out / check-in") — backs the Check-out
/// tab. Tenant-wide; user-only (a ServiceAccount holds no locks, so it sees an empty list). Each item carries
/// the current confirmed version's SHA-256 so the client can compare it against its local working copy to tell
/// whether it was edited (offer Check in / Discard) or is untouched (offer Unlock).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/checkouts")]
[Authorize]
public class CheckoutsController : ControllerBase
{
    private static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(15);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IObjectStorageClient _objectStorage;
    private readonly Documents.DocumentFinalizer _finalizer;
    private readonly ILegalHoldService _legalHold;
    private readonly IAuditRecorder _audit;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IDocumentVersionComparer _comparer;

    public CheckoutsController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IObjectStorageClient objectStorage,
        Documents.DocumentFinalizer finalizer,
        ILegalHoldService legalHold,
        IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights,
        IDocumentVersionComparer comparer)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _objectStorage = objectStorage;
        _finalizer = finalizer;
        _legalHold = legalHold;
        _audit = audit;
        _userSystemRights = userSystemRights;
        _comparer = comparer;
    }

    // The per-user working-copy stash key (ADR "Check-out working-copy stash + exit guard"): a durable home for
    // in-progress edits that survives logout/close and is re-downloaded on next login. Keyed by the holder's
    // user id + document id (no extension — the client names the local file from the checkout's FileExtension).
    public static string StashKey(Guid tenantId, Guid userId, Guid documentId) =>
        CheckoutStashKey.Build(tenantId, userId, documentId);

    public class CheckoutResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        // The current confirmed version's SHA-256 (hex) — compared against the local working copy's hash.
        public string Sha256 { get; set; } = "";
        // The current version's file extension (Document.Name is a bare stem, ADR 0277) — the working-copy filename.
        public string FileExtension { get; set; } = "";
        public DateTimeOffset CheckedOutAt { get; set; }

        // When an idle check-out will be auto-released (CheckedOutAt + the tenant's CheckoutTtlDays), null when
        // auto-release is disabled (ADR "Check-out expiry UX"). Drives the Check-out tab's expiry column.
        public DateTimeOffset? ExpiresAt { get; set; }

        // A cloud working-copy stash exists for this check-out (ADR "Check-out working-copy stash") — the client
        // re-downloads it on login. StashDownloadUrl is a presigned GET, present only when HasStash.
        public bool HasStash { get; set; }

        // The working copy in check-out (the cloud stash) differs from the current version — i.e. there's an edit to
        // check in. Computed server-side by hashing the stash vs Sha256 (ADR 0513); both clients gate Check-in on it.
        public bool IsModified { get; set; }

        public string? StashDownloadUrl { get; set; }

        // A presigned download of the current repository version — the web "Download from stash" falls back to
        // this when there's no stash yet (the initial working copy).
        public string? DownloadUrl { get; set; }
    }

    public class CheckoutsResource : HypermediaResource
    {
        public List<CheckoutResource> Items { get; set; } = [];
    }

    public class WorkingCopyUploadResource : HypermediaResource
    {
        public Uri UploadUrl { get; set; } = null!;
    }

    // Inline unified text diff of the current version vs the working copy in check-out (the stash) — ADR 0513 slice 3.
    public class CheckoutComparisonResource : HypermediaResource
    {
        // False when there's no stash (nothing changed) or a side has no extractable text (binary/image format).
        public bool Available { get; set; }
        public List<CheckoutDiffLine> Lines { get; set; } = [];
    }

    public class CheckoutDiffLine
    {
        public int Op { get; set; } // matches DiffOp: 0 unchanged, 1 inserted, 2 deleted
        public string Text { get; set; } = "";
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var items = await BuildAsync(cancellationToken);
        return Ok(new CheckoutsResource
        {
            Items = items,
            Links = [new Link("self", "/api/checkouts", "GET")],
        });
    }

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken)
    {
        await BuildAsync(cancellationToken);
        return NoContent();
    }

    // Inline unified text diff of the current version vs the working copy in check-out (the cloud stash) — ADR 0513
    // slice 3, the "Compare" action. Holder-only. Reuses IDocumentVersionComparer (which works on object keys, so
    // the stash is just another key). Available is false when there's no stash or a side has no extractable text.
    [HttpGet("{documentId:guid}/compare")]
    public async Task<IActionResult> Compare(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (document.CheckedOutByUserId != userId)
        {
            return Forbid(); // only the lock holder may compare their working copy
        }

        var selfLink = new Link("self", $"/api/checkouts/{documentId}/compare", "GET");
        var stashKey = StashKey(tenantId, userId, documentId);
        var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, document.CurrentVersionId, cancellationToken);
        if (version is null || !await _objectStorage.ExistsAsync(stashKey, cancellationToken))
        {
            return Ok(new CheckoutComparisonResource { Available = false, Lines = [], Links = [selfLink] });
        }

        // The stash key is extensionless (ADR 0517) — hint the current version's extension so a text-file working
        // copy decodes directly rather than depending on Tika.
        var comparison = await _comparer.CompareAsync(version.ObjectKey, stashKey, System.IO.Path.GetExtension(version.ObjectKey), cancellationToken);
        return Ok(new CheckoutComparisonResource
        {
            Available = comparison.Available,
            Lines = comparison.Lines.Select(l => new CheckoutDiffLine { Op = (int)l.Op, Text = l.Text }).ToList(),
            Links = [selfLink],
        });
    }

    [HttpHead("{documentId:guid}/compare")]
    public async Task<IActionResult> CompareHead(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        return document is null ? NotFound()
            : document.CheckedOutByUserId == userId ? NoContent()
            : Forbid();
    }

    private async Task<List<CheckoutResource>> BuildAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return [];
        }

        var docs = await _dbContext.Documents
            .Where(d => d.CheckedOutByUserId == userId)
            .OrderByDescending(d => d.CheckedOutAt)
            .Select(d => new { d.Id, d.Name, d.ParentId, d.CurrentVersionId, CheckedOutAt = d.CheckedOutAt!.Value })
            .ToListAsync(cancellationToken);

        var tenantId = _currentTenantAccessor.TenantId;
        // The tenant's auto-release TTL, to compute each check-out's expiry (0 = disabled → no expiry shown).
        var ttlDays = tenantId is { } t
            ? await _dbContext.Tenants.Where(x => x.Id == t).Select(x => x.CheckoutTtlDays).FirstOrDefaultAsync(cancellationToken)
            : 0;
        var items = new List<CheckoutResource>();
        foreach (var d in docs)
        {
            // The document's current version honoring the pointer (issue #265), else the latest confirmed.
            var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, d.Id, d.CurrentVersionId, cancellationToken);

            var downloadUrl = version is null
                ? null
                : (await _objectStorage.GetPresignedDownloadUrlAsync(version.ObjectKey, PresignedUrlExpiry, cancellationToken: cancellationToken)).ToString();

            // Is there a cloud stash (in-progress working copy) for this check-out? If so, offer its download URL
            // so the client restores it on login, and decide whether it's actually MODIFIED — the working copy in
            // check-out differs from the current version — by hashing the stash and comparing to the version's
            // SHA-256 (the single source of "modified" for both clients; the desktop no longer keeps a local copy).
            var hasStash = false;
            var isModified = false;
            string? stashDownloadUrl = null;
            if (tenantId is { } tid)
            {
                var stashKey = StashKey(tid, userId, d.Id);
                hasStash = await _objectStorage.ExistsAsync(stashKey, cancellationToken);
                if (hasStash)
                {
                    stashDownloadUrl = (await _objectStorage.GetPresignedDownloadUrlAsync(stashKey, PresignedUrlExpiry, cancellationToken: cancellationToken)).ToString();
                    if (version is not null)
                    {
                        await using var stashStream = await _objectStorage.GetObjectAsync(stashKey, cancellationToken);
                        var stashSha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stashStream, cancellationToken));
                        isModified = !string.Equals(stashSha, version.Sha256Hash, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            items.Add(new CheckoutResource
            {
                Id = d.Id,
                Name = d.Name,
                Path = await BuildPathAsync(d.ParentId, cancellationToken),
                Sha256 = version?.Sha256Hash ?? "",
                FileExtension = System.IO.Path.GetExtension(version?.ObjectKey ?? ""),
                CheckedOutAt = d.CheckedOutAt,
                ExpiresAt = ttlDays > 0 ? d.CheckedOutAt.AddDays(ttlDays) : null,
                HasStash = hasStash,
                IsModified = isModified,
                StashDownloadUrl = stashDownloadUrl,
                DownloadUrl = downloadUrl,
                Links =
                [
                    new Link("self", $"/api/documents/{d.Id}", "GET"),
                    new Link("checkin", $"/api/documents/{d.Id}/checkout", "DELETE"),
                    new Link("working-copy", $"/api/checkouts/{d.Id}/working-copy", "POST"),
                    new Link("extend", $"/api/checkouts/{d.Id}/extend", "POST"),
                ],
            });
        }

        return items;
    }

    // "Save to cloud" — a presigned PUT to the working-copy stash, so in-progress edits survive logout/close and
    // are re-downloaded on next login (ADR "Check-out working-copy stash + exit guard"). Holder-only: the caller
    // must currently hold the lock on this document.
    [HttpPost("{documentId:guid}/working-copy")]
    public async Task<IActionResult> UploadWorkingCopy(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var holder = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => (Guid?)d.CheckedOutByUserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (holder is null)
        {
            return NotFound();
        }

        if (holder != userId)
        {
            return Forbid(); // only the lock holder may stash a working copy
        }

        var uploadUrl = await _objectStorage.GetPresignedUploadUrlAsync(StashKey(tenantId, userId, documentId), PresignedUrlExpiry, cancellationToken);
        return Ok(new WorkingCopyUploadResource
        {
            UploadUrl = uploadUrl,
            Links = [new Link("self", $"/api/checkouts/{documentId}/working-copy", "POST")],
        });
    }

    // "Extend my check-out" (ADR "Self-service check-out extension") — resets the idle timer by touching
    // CheckedOutAt to now, so the tenant's auto-release TTL restarts (ExpiresAt = now + CheckoutTtlDays) and the
    // "expiring soon" grace warning clears (CheckoutReminderSentAt). No new version, no stash change. Permitted for
    // the lock holder OR a CanOverrideCheckout admin (who can already break the lock, so extending it is lesser).
    // Idempotent-ish: each call just re-stamps CheckedOutAt to now. A ServiceAccount holds no locks (403).
    [HttpPost("{documentId:guid}/extend")]
    public async Task<IActionResult> Extend(Guid documentId, CancellationToken cancellationToken)
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
            throw new CheckoutNotHeldException(); // nothing to extend
        }

        var isOverride = holder != userId;
        if (isOverride && !(await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanOverrideCheckout)
        {
            return Forbid(); // only the holder or a CanOverrideCheckout holder may extend
        }

        document.CheckedOutAt = DateTimeOffset.UtcNow;
        document.CheckoutReminderSentAt = null; // clears the "expiring soon" grace warning so it can re-fire later
        await _dbContext.SaveChangesAsync(cancellationToken);

        var holderName = await _dbContext.Users.Where(u => u.Id == holder).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken);
        await _audit.RecordAsync(
            AuditActions.DocumentCheckoutExtended, "Document", documentId, document.Name,
            isOverride ? $"extended the check-out held by {holderName}" : "extended the check-out",
            cancellationToken: cancellationToken);

        return NoContent();
    }

    // Check in from the cloud stash (ADR "Check-out working-copy stash + exit guard") — promotes the current
    // stash to a new confirmed version (server-side copy + finalize, keeping the mask), releases the lock, and
    // deletes the stash. The web check-in path (the browser has no local file to upload as a version): the user
    // uploads their edited file to the stash first, then this commits it. Holder-only; 400 if there's no stash.
    [HttpPost("{documentId:guid}/checkin")]
    public async Task<IActionResult> CheckInFromStash(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        if (document.CheckedOutByUserId != userId)
        {
            return Forbid(); // only the lock holder may check in
        }

        if (await _legalHold.IsFrozenAsync(documentId, cancellationToken))
        {
            throw new DocumentUnderLegalHoldException();
        }

        var stashKey = StashKey(tenantId, userId, documentId);
        if (!await _objectStorage.ExistsAsync(stashKey, cancellationToken))
        {
            throw new NoStashException();
        }

        // The current version's extension keeps the new version's stored object typed correctly (the pinned
        // version if CurrentVersionId is set — issue #265 — else the latest confirmed).
        var pointer = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.CurrentVersionId).FirstOrDefaultAsync(cancellationToken);
        var currentVersion = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, pointer, cancellationToken);
        var currentExtension = System.IO.Path.GetExtension(currentVersion?.ObjectKey ?? "");

        var now = DateTimeOffset.UtcNow;
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, currentExtension);
        await _objectStorage.CopyObjectAsync(stashKey, objectKey, cancellationToken);

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };

        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _finalizer.FinalizeAsync(version, cancellationToken); // no staged draft — the existing document keeps its mask

        // Release the lock + clear the stash (it became the new version).
        document.CheckedOutByUserId = null;
        document.CheckedOutAt = null;
        document.CheckoutReminderSentAt = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _objectStorage.DeleteObjectAsync(stashKey, cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentCheckedIn, "Document", documentId, document.Name, cancellationToken: cancellationToken);
        return NoContent();
    }

    // The containing-folder path ("Repositories / Contracts / 2026") — the ancestor chain, like the recycle bin.
    private async Task<string> BuildPathAsync(Guid? parentId, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var current = parentId;
        while (current is { } id)
        {
            var node = await _dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Name, d.ParentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (node is null)
            {
                break;
            }

            segments.Insert(0, node.Name);
            current = node.ParentId;
        }

        return string.Join(" / ", segments);
    }
}
