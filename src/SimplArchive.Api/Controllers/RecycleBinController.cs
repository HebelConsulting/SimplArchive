using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The tenant-wide recycle bin (ADR "Recycle bin tab") — every top-level soft-deleted item the caller can see,
/// across all repositories, for the dedicated Recycle Bin tab. Lists the deletion roots (a soft-deleted
/// document whose parent is not itself soft-deleted); restoring or purging a root handles its whole subtree.
/// "Deleted by" is derived from the audit trail (the latest Document.Deleted / RetentionDisposed event) — "—"
/// when unavailable. Per-item restore/purge are the existing document endpoints; "empty" (purge-all) is here,
/// tenant-admin-only, like the per-repository empty.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/recycle-bin")]
[Authorize]
public class RecycleBinController : ControllerBase
{
    private const int MaxItems = 500;
    private static readonly string[] SoftDeleteFilterOnly = ["SoftDeleteFilter"];

    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRights;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IAuditRecorder _audit;
    private readonly Documents.DocumentPurger _purger;
    private readonly Documents.DocumentRestorer _restorer;
    private readonly Documents.MailboxAddressClaims _mailboxAddressClaims;

    public RecycleBinController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRights,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IAuditRecorder audit,
        Documents.DocumentPurger purger,
        Documents.DocumentRestorer restorer,
        Documents.MailboxAddressClaims mailboxAddressClaims)
    {
        _dbContext = dbContext;
        _effectiveRights = effectiveRights;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _audit = audit;
        _purger = purger;
        _restorer = restorer;
        _mailboxAddressClaims = mailboxAddressClaims;
    }

    public class RecycleBinResource : HypermediaResource
    {
        public List<RecycleBinItemResource> Items { get; set; } = [];
        public bool Truncated { get; set; }
    }

    public class RecycleBinItemResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public DateTimeOffset DeletedAt { get; set; }
        public string DeletedBy { get; set; } = string.Empty;
    }

    public class RestoreManyRequest
    {
        public List<Guid> Ids { get; set; } = [];
    }

    public class RestoreManyResource : HypermediaResource
    {
        // How many of the requested ids were actually restored, and how many were skipped (already active,
        // gone, or not restorable by the caller). The client refreshes the list either way.
        public int Restored { get; set; }
        public int Skipped { get; set; }
    }

    public class PurgeSelectedRequest
    {
        public List<Guid> Ids { get; set; } = [];
    }

    public class PurgeSelectedResource : HypermediaResource
    {
        // How many of the requested ids were permanently purged, and how many were skipped (gone, still active,
        // under a legal hold, or WORM-locked). The client refreshes the list either way.
        public int Purged { get; set; }
        public int Skipped { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var isTenantAdmin = await IsTenantAdminAsync(cancellationToken);

        // Every soft-deleted document the caller can see (tenant-wide), newest-first. Each is individually
        // inspectable/restorable/purgeable — including a lone deleted child (restoring it while its parent
        // stays deleted lands it in "Recovered Items", ADR 0196).
        var roots = await _dbContext.Documents
            .IgnoreQueryFilters(SoftDeleteFilterOnly)
            .Where(d => d.DeletedAt != null)
            .OrderByDescending(d => d.DeletedAt)
            .Select(d => new { d.Id, d.Name, d.ParentId, DeletedAt = d.DeletedAt!.Value })
            .Take(MaxItems + 1)
            .ToListAsync(cancellationToken);

        var truncated = roots.Count > MaxItems;
        roots = roots.Take(MaxItems).ToList();

        // Deleted-by, from the audit trail (batched); "—" when there's no recorded event.
        var ids = roots.Select(r => r.Id).ToList();
        var deletedBy = await ResolveDeletedByAsync(ids, cancellationToken);

        var items = new List<RecycleBinItemResource>();
        foreach (var root in roots)
        {
            // A tenant admin sees everything (ACL bypass); otherwise filter by CanSee on the item.
            if (!isTenantAdmin && !await CanSeeAsync(root.Id, cancellationToken))
            {
                continue;
            }

            items.Add(new RecycleBinItemResource
            {
                Id = root.Id,
                Name = root.Name,
                Path = await BuildPathAsync(root.ParentId, cancellationToken),
                DeletedAt = root.DeletedAt,
                DeletedBy = deletedBy.GetValueOrDefault(root.Id, "—"),
                Links =
                [
                    new Link("restore", $"/api/documents/{root.Id}/restore", "POST"),
                    new Link("purge", $"/api/documents/{root.Id}/purge", "POST"),

                    // What the client shows when a deleted item is SELECTED: its mask, index data, chat and
                    // versions (the last carrying the preview/text-layout rels for the pane). They ride on the
                    // row on purpose — a listing's addresses arrive with the listing and cost nothing, whereas
                    // advertising only `self` would spend one fetch per selection just to learn four addresses
                    // that were already known here (ADR 0557). Reading a soft-deleted document's detail is
                    // exactly what the recycle bin is for, so these are as much part of the row as restore.
                    new Link("mask", $"/api/documents/{root.Id}/mask", "GET"),
                    new Link("index-data", $"/api/documents/{root.Id}/index-data", "GET"),
                    new Link("chat", $"/api/documents/{root.Id}/chat", "GET"),
                    new Link("versions", $"/api/documents/{root.Id}/versions", "GET"),
                ],
            });
        }

        var links = new List<Link>
        {
            new("self", "/api/recycle-bin", "GET"),
            new("restore-selected", "/api/recycle-bin/restore", "POST"),
            new("purge-selected", "/api/recycle-bin/purge-selected", "POST"),
            new("purge-all", "/api/recycle-bin/purge", "POST"),
        };

        return Ok(new RecycleBinResource { Items = items, Truncated = truncated, Links = links });
    }

    [HttpHead]
    public IActionResult Head() => NoContent();

    // Bulk restore (ADR "Bulk restore from the recycle bin") — restores each requested soft-deleted document +
    // its subtree in one call (mirroring the empty-all purge). Each id is restored only when the caller can see
    // it (a tenant admin bypasses) and holds CanDelete on it (the same right the per-item restore reuses); an
    // id that's missing, already active, or not permitted is silently skipped and counted. Reports how many were
    // restored vs skipped; the client refreshes the list. A ServiceAccount can act if it holds the rights.
    [HttpPost("restore")]
    public async Task<IActionResult> RestoreMany([FromBody] RestoreManyRequest request, CancellationToken cancellationToken)
    {
        var isTenantAdmin = await IsTenantAdminAsync(cancellationToken);
        var (callerUserId, callerServiceAccountId) = CallerIdentity();

        var restored = 0;
        var skipped = 0;
        foreach (var id in request.Ids.Distinct())
        {
            var document = await _dbContext.Documents
                .IgnoreQueryFilters(SoftDeleteFilterOnly)
                .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

            // Skip anything gone, not visible, or not deletable by the caller (restore reuses CanDelete, ADR 0196).
            // A subtree containing a mailbox additionally needs the routing right (#703) — SKIPPED here rather
            // than failing the whole call, because skip-and-count is this endpoint's own contract for
            // everything the caller may not restore.
            if (document is null
                || (!isTenantAdmin && !await CanSeeAsync(id, cancellationToken))
                || !await CanDeleteAsync(id, cancellationToken)
                || (await _mailboxAddressClaims.SubtreeContainsMailboxAsync(id, cancellationToken)
                    && !await _mailboxAddressClaims.CallerMayRouteAsync(cancellationToken)))
            {
                skipped++;
                continue;
            }

            if (await _restorer.RestoreAsync(document, callerUserId, callerServiceAccountId, cancellationToken))
            {
                await _audit.RecordAsync(AuditActions.DocumentRestored, "Document", id, document.Name, cancellationToken: cancellationToken);
                restored++;
            }
            else
            {
                skipped++; // already active
            }
        }

        return Ok(new RestoreManyResource
        {
            Restored = restored,
            Skipped = skipped,
            Links = [new Link("recycle-bin", "/api/recycle-bin", "GET")],
        });
    }

    // Empties the tenant's recycle bin — purges every top-level soft-deleted root (each cascading its subtree),
    // irreversibly. Tenant-admin-only; skips any item somehow under a legal hold. See ADR "Manual hard-delete /
    // purge" / "Recycle bin tab".
    [HttpPost("purge")]
    public async Task<IActionResult> EmptyRecycleBin(CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var softDeleted = await _dbContext.Documents
            .IgnoreQueryFilters(SoftDeleteFilterOnly)
            .Where(d => d.DeletedAt != null)
            .Select(d => new { d.Id, d.ParentId })
            .ToListAsync(cancellationToken);

        var deletedIds = softDeleted.Select(d => d.Id).ToHashSet();
        var rootIds = softDeleted.Where(d => d.ParentId is not { } pid || !deletedIds.Contains(pid)).Select(d => d.Id);

        var toPurge = new List<Document>();
        foreach (var rootId in rootIds)
        {
            if (await _purger.CollectSubtreeAsync(rootId, cancellationToken) is { } subtree)
            {
                if (!await _purger.AnyHeldAsync(subtree.Select(d => d.Id).ToList(), cancellationToken))
                {
                    toPurge.AddRange(subtree);
                }
            }
        }

        var purged = await _purger.PurgeAsync(toPurge, cancellationToken);
        foreach (var (id, name) in purged)
        {
            await _audit.RecordAsync(AuditActions.DocumentPurged, "Document", id, name, cancellationToken: cancellationToken);
        }

        return NoContent();
    }

    // Bulk purge of selected items (ADR "Bulk purge of selected recycle-bin items") — permanently removes each
    // requested recycle-bin root + its subtree, irreversibly. Tenant-admin-only. A selected id that's gone, still
    // active (not in the bin), under an active legal hold, or WORM-locked is **silently skipped** (not the whole
    // request refused), reporting { purged, skipped }. Purged per-id so one protected item can't abort the batch.
    [HttpPost("purge-selected")]
    public async Task<IActionResult> PurgeSelected([FromBody] PurgeSelectedRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var purged = 0;
        var skipped = 0;
        foreach (var id in request.Ids.Distinct())
        {
            var subtree = await _purger.CollectSubtreeAsync(id, cancellationToken);
            // Skip anything gone or still active (not in the recycle bin) — only a soft-deleted item is purgeable.
            if (subtree is null || subtree[0].DeletedAt is null)
            {
                skipped++;
                continue;
            }

            // Skip a subtree under an active legal hold.
            if (await _purger.AnyHeldAsync(subtree.Select(d => d.Id).ToList(), cancellationToken))
            {
                skipped++;
                continue;
            }

            try
            {
                var done = await _purger.PurgeAsync(subtree, cancellationToken);
                foreach (var (pid, name) in done)
                {
                    await _audit.RecordAsync(AuditActions.DocumentPurged, "Document", pid, name, cancellationToken: cancellationToken);
                }

                purged++;
            }
            catch (WormLockedException)
            {
                skipped++; // a version blob is still under an unexpired retention / object legal hold
            }
        }

        return Ok(new PurgeSelectedResource
        {
            Purged = purged,
            Skipped = skipped,
            Links = [new Link("recycle-bin", "/api/recycle-bin", "GET")],
        });
    }

    private async Task<Dictionary<Guid, string>> ResolveDeletedByAsync(List<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var events = await _dbContext.AuditEvents
            .Where(a => a.TargetId != null && documentIds.Contains(a.TargetId.Value)
                && (a.Action == AuditActions.DocumentDeleted || a.Action == AuditActions.DocumentRetentionDisposed))
            .Select(a => new { a.TargetId, a.ActorName, a.Timestamp })
            .ToListAsync(cancellationToken);

        return events
            .GroupBy(e => e.TargetId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Timestamp).First().ActorName);
    }

    // "Repositories / Folder / Subfolder" — the deleted item's location, walking up ParentId (ignoring the
    // soft-delete filter so a deleted ancestor still contributes to the path).
    private async Task<string> BuildPathAsync(Guid? parentId, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var current = parentId;
        while (current is { } id)
        {
            var node = await _dbContext.Documents
                .IgnoreQueryFilters(["SoftDeleteFilter"])
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

    private async Task<bool> CanSeeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (await _effectiveRights.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken)).CanSee;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _effectiveRights.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee;
        }

        return false;
    }

    // Restore reuses CanDelete (the delete's inverse right, ADR 0196) rather than a new one.
    private async Task<bool> CanDeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (await _effectiveRights.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken)).CanDelete;
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _effectiveRights.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanDelete;
        }

        return false;
    }

    private (Guid? UserId, Guid? ServiceAccountId) CallerIdentity() =>
        _currentServiceAccountAccessor.ServiceAccountId is { } sa ? (null, sa) : (_currentUserAccessor.UserId, null);

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
        }

        return false;
    }
}
