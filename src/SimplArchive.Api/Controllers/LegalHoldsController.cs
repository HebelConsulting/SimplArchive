using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.LegalHolds;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Legal holds / litigation matters (ADR "Legal hold & retention enforcement"). A hold is a named matter that
/// covers a set of documents; while a document is covered by an active hold (directly or via an ancestor), it
/// is frozen — it can't be deleted, moved, renamed, re-versioned, or have its metadata changed (enforced at the
/// mutation sites via <see cref="ILegalHoldService"/>). Every action here is gated on the caller's own
/// <c>CanLegalHold</c> — a User-only right (a ServiceAccount has none). Placing a hold does not require CanSee
/// on the document: legal hold is a compliance action that overrides per-document ACL.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/legal-holds")]
[Authorize]
public class LegalHoldsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IAuditRecorder _audit;

    public LegalHoldsController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IAuditRecorder audit,
        IWormLockService wormLock)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _audit = audit;
        _wormLock = wormLock;
    }

    private readonly IWormLockService _wormLock;

    public class LegalHoldResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Reason { get; set; }
        public DateTimeOffset PlacedAt { get; set; }
        public DateTimeOffset? ReleasedAt { get; set; }
        public bool IsActive { get; set; }
        public int ItemCount { get; set; }
        public List<LegalHoldItemResource> Items { get; set; } = [];
    }

    public class LegalHoldItemResource : HypermediaResource
    {
        public Guid DocumentId { get; set; }
        public string DocumentName { get; set; } = "";
    }

    public class LegalHoldsListResource : HypermediaResource
    {
        public List<LegalHoldResource> Holds { get; set; } = [];
    }

    public class CreateLegalHoldRequest
    {
        public string Name { get; set; } = "";
        public string? Reason { get; set; }
    }

    public class AddItemRequest
    {
        public Guid DocumentId { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLegalHoldRequest request, CancellationToken cancellationToken)
    {
        if (await CurrentUserIdIfCanLegalHoldAsync(cancellationToken) is not { } userId)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new LegalHoldNameRequiredException();
        }

        var hold = new LegalHold
        {
            Id = Guid.NewGuid(),
            TenantId = _currentTenantAccessor.TenantId!.Value,
            Name = request.Name.Trim(),
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            PlacedByUserId = userId,
            PlacedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.LegalHolds.Add(hold);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.LegalHoldPlaced, "LegalHold", hold.Id, hold.Name, cancellationToken: cancellationToken);

        var resource = await BuildResourceAsync(hold.Id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { holdId = hold.Id }, resource);
    }

    // Cursor-based pagination (?cursor=&limit=), sorted PlacedAt ascending, Id ascending — see ADR "Pagination
    // for list endpoints".
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (!await CanLegalHoldAsync(cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);
        var query = _dbContext.LegalHolds.AsQueryable();
        if (Cursor.TryDecode(cursor, out var cursorPlacedAt, out var cursorId))
        {
            query = query.Where(h => h.PlacedAt > cursorPlacedAt || (h.PlacedAt == cursorPlacedAt && h.Id > cursorId));
        }

        var fetched = await query.OrderBy(h => h.PlacedAt).ThenBy(h => h.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };
        if (hasMore)
        {
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = Cursor.Encode(page[^1].PlacedAt, page[^1].Id), limit = pageSize })!, "GET"));
        }

        var resources = new List<LegalHoldResource>();
        foreach (var hold in page)
        {
            resources.Add(await BuildResourceAsync(hold.Id, cancellationToken, includeItems: false));
        }

        return Ok(new LegalHoldsListResource { Holds = resources, Links = links });
    }

    [HttpGet("{holdId:guid}")]
    public async Task<IActionResult> Get(Guid holdId, CancellationToken cancellationToken)
    {
        if (!await CanLegalHoldAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!await _dbContext.LegalHolds.AnyAsync(h => h.Id == holdId, cancellationToken))
        {
            return NotFound();
        }

        return Ok(await BuildResourceAsync(holdId, cancellationToken));
    }

    [HttpHead("{holdId:guid}")]
    public async Task<IActionResult> Head(Guid holdId, CancellationToken cancellationToken)
    {
        if (!await CanLegalHoldAsync(cancellationToken))
        {
            return Forbid();
        }

        return await _dbContext.LegalHolds.AnyAsync(h => h.Id == holdId, cancellationToken) ? NoContent() : NotFound();
    }

    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken) =>
        await CanLegalHoldAsync(cancellationToken) ? NoContent() : Forbid();

    [HttpPost("{holdId:guid}/items")]
    public async Task<IActionResult> AddItem(Guid holdId, [FromBody] AddItemRequest request, CancellationToken cancellationToken)
    {
        if (!await CanLegalHoldAsync(cancellationToken))
        {
            return Forbid();
        }

        var hold = await _dbContext.LegalHolds.SingleOrDefaultAsync(h => h.Id == holdId, cancellationToken);
        if (hold is null)
        {
            return NotFound();
        }

        if (hold.ReleasedAt is not null)
        {
            throw new LegalHoldReleasedException();
        }

        var document = await _dbContext.Documents.SingleOrDefaultAsync(d => d.Id == request.DocumentId, cancellationToken);
        if (document is null)
        {
            throw DocumentNotFoundException.NotFound();
        }

        if (await _dbContext.LegalHoldItems.AnyAsync(i => i.LegalHoldId == holdId && i.DocumentId == request.DocumentId, cancellationToken))
        {
            throw new LegalHoldItemExistsException();
        }

        _dbContext.LegalHoldItems.Add(new LegalHoldItem
        {
            Id = Guid.NewGuid(),
            TenantId = _currentTenantAccessor.TenantId!.Value,
            LegalHoldId = holdId,
            DocumentId = request.DocumentId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.LegalHoldItemAdded, "Document", document.Id, document.Name, $"Hold '{hold.Name}'", cancellationToken: cancellationToken);
        await _wormLock.ReconcileAsync(request.DocumentId, cancellationToken); // apply the WORM legal-hold lock

        return Ok(await BuildResourceAsync(holdId, cancellationToken));
    }

    [HttpDelete("{holdId:guid}/items/{documentId:guid}")]
    public async Task<IActionResult> RemoveItem(Guid holdId, Guid documentId, CancellationToken cancellationToken)
    {
        if (!await CanLegalHoldAsync(cancellationToken))
        {
            return Forbid();
        }

        var item = await _dbContext.LegalHoldItems.SingleOrDefaultAsync(i => i.LegalHoldId == holdId && i.DocumentId == documentId, cancellationToken);
        if (item is null)
        {
            return NotFound();
        }

        _dbContext.LegalHoldItems.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var name = await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.LegalHoldItemRemoved, "Document", documentId, name, cancellationToken: cancellationToken);
        await _wormLock.ReconcileAsync(documentId, cancellationToken); // lift the WORM legal-hold lock if no other active hold covers it

        return NoContent();
    }

    // Releases the whole matter (POST, a state transition, per the RESTful-naming convention). Idempotent — a
    // second release is a no-op. A document stays frozen if any OTHER active hold still covers it.
    [HttpPost("{holdId:guid}/release")]
    public async Task<IActionResult> Release(Guid holdId, CancellationToken cancellationToken)
    {
        if (await CurrentUserIdIfCanLegalHoldAsync(cancellationToken) is not { } userId)
        {
            return Forbid();
        }

        var hold = await _dbContext.LegalHolds.SingleOrDefaultAsync(h => h.Id == holdId, cancellationToken);
        if (hold is null)
        {
            return NotFound();
        }

        if (hold.ReleasedAt is null)
        {
            var itemDocumentIds = await _dbContext.LegalHoldItems
                .Where(i => i.LegalHoldId == holdId)
                .Select(i => i.DocumentId)
                .ToListAsync(cancellationToken);

            hold.ReleasedAt = DateTimeOffset.UtcNow;
            hold.ReleasedByUserId = userId;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync(AuditActions.LegalHoldReleased, "LegalHold", hold.Id, hold.Name, cancellationToken: cancellationToken);

            // Re-evaluate each covered document's WORM legal-hold lock — lifted unless another active hold covers it.
            foreach (var documentId in itemDocumentIds)
            {
                await _wormLock.ReconcileAsync(documentId, cancellationToken);
            }
        }

        return Ok(await BuildResourceAsync(holdId, cancellationToken));
    }

    private async Task<LegalHoldResource> BuildResourceAsync(Guid holdId, CancellationToken cancellationToken, bool includeItems = true)
    {
        var hold = await _dbContext.LegalHolds.SingleAsync(h => h.Id == holdId, cancellationToken);
        var itemCount = await _dbContext.LegalHoldItems.CountAsync(i => i.LegalHoldId == holdId, cancellationToken);

        var items = includeItems
            ? await (from i in _dbContext.LegalHoldItems
                     where i.LegalHoldId == holdId
                     join d in _dbContext.Documents on i.DocumentId equals d.Id
                     orderby d.Name
                     select new LegalHoldItemResource { DocumentId = d.Id, DocumentName = d.Name }).ToListAsync(cancellationToken)
            : [];

        // A covered document's own `remove` address (issue #416). It is the ITEM that knows both ends of the
        // pairing, so without it a client holding the list has two ids and no address, and had to compose the
        // path. Only while the hold is active — a released hold's items are history, not something to edit, so
        // the rel's absence is the answer rather than a refusal after the click (ADR 0543).
        if (hold.ReleasedAt is null)
        {
            foreach (var item in items)
            {
                item.Links = [new Link("remove", $"/api/legal-holds/{holdId}/items/{item.DocumentId}", "DELETE")];
            }
        }

        var links = new List<Link> { new("self", $"/api/legal-holds/{holdId}", "GET") };
        if (hold.ReleasedAt is null)
        {
            links.Add(new Link("release", $"/api/legal-holds/{holdId}/release", "POST"));
            links.Add(new Link("add-item", $"/api/legal-holds/{holdId}/items", "POST"));
        }

        return new LegalHoldResource
        {
            Id = hold.Id,
            Name = hold.Name,
            Reason = hold.Reason,
            PlacedAt = hold.PlacedAt,
            ReleasedAt = hold.ReleasedAt,
            IsActive = hold.ReleasedAt is null,
            ItemCount = itemCount,
            Items = items,
            Links = links,
        };
    }

    // The caller's own effective CanLegalHold — a User-only right (a ServiceAccount / PlatformAdministrator
    // caller has none), unioned with the caller's groups (ADR "Enforce group system rights for members").
    private async Task<bool> CanLegalHoldAsync(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanLegalHold;
        }

        return false;
    }

    private async Task<Guid?> CurrentUserIdIfCanLegalHoldAsync(CancellationToken cancellationToken) =>
        await CanLegalHoldAsync(cancellationToken) && _currentUserAccessor.UserId is { } userId ? userId : null;
}
