using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Audit;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Audit;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Reads the append-only audit log (ADR "Audit trail (first slice)"). Tenant-scoped (the query filter),
/// newest-first, cursor-paginated, with optional filters (actor, action, target, date range). Gated on the
/// dedicated <c>CanViewAuditLog</c> right — a logged-in User (own ∪ groups, via the resolver) or a
/// ServiceAccount. Read-only: there is no write endpoint (events are recorded by <see cref="IAuditRecorder"/>
/// at the mutation sites) and no delete (append-only; retention/purge deferred).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/audit-events")]
[Authorize]
public class AuditEventsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IAuditChainVerifier _chainVerifier;
    private readonly IAuditWormVerifier _wormVerifier;
    private readonly IAuditRetentionService _retentionService;

    public AuditEventsController(
        SimplArchiveDbContext dbContext,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights,
        IAuditChainVerifier chainVerifier,
        IAuditWormVerifier wormVerifier,
        IAuditRetentionService retentionService,
        IObjectStorageClient objectStorage)
    {
        _dbContext = dbContext;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
        _chainVerifier = chainVerifier;
        _wormVerifier = wormVerifier;
        _retentionService = retentionService;
        _objectStorage = objectStorage;
    }

    private readonly IObjectStorageClient _objectStorage;

    public class AuditEventResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public DateTimeOffset Timestamp { get; set; }

        public string ActorType { get; set; } = "";

        public Guid ActorId { get; set; }

        public string ActorName { get; set; } = "";

        public string Action { get; set; } = "";

        public string? TargetType { get; set; }

        public Guid? TargetId { get; set; }

        public string? TargetName { get; set; }

        public string? Details { get; set; }
    }

    public class AuditEventsListResource : HypermediaResource
    {
        public List<AuditEventResource> Events { get; set; } = [];
    }

    public class AuditChainVerificationResource : HypermediaResource
    {
        // True = the tenant's hash chain recomputed cleanly (no edit/deletion/reorder detected).
        public bool Valid { get; set; }

        // Number of events walked.
        public int CheckedCount { get; set; }

        // The Sequence at which the first break was found (null when Valid).
        public long? BrokenAtSequence { get; set; }
    }

    public class AuditWormVerificationResource : HypermediaResource
    {
        // True = the sealed WORM segments are contiguous and every sealed event's hash matches the DB.
        public bool Valid { get; set; }

        // Sealed segment objects read.
        public int SegmentCount { get; set; }

        // Sealed events walked.
        public int CheckedCount { get; set; }

        // The Sequence where the first break was found (null when Valid).
        public long? BrokenAtSequence { get; set; }

        // The break kind (null when Valid): "segment-gap", "db-mismatch" (a DB tamper the immutable segment
        // caught), or "missing-segment".
        public string? Reason { get; set; }
    }

    public class RetentionResource : HypermediaResource
    {
        // Purge events older than this many days; 0 = keep forever.
        public int RetentionDays { get; set; }

        // The retained window's chain start (> 0 once the oldest events have been purged).
        public long ChainStartSequence { get; set; }

        public DateTimeOffset? LastPurgedAt { get; set; }
    }

    public class SetRetentionRequest
    {
        public int RetentionDays { get; set; }
    }

    public class PurgeResource : HypermediaResource
    {
        public int PurgedCount { get; set; }

        public long ChainStartSequence { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromQuery] Guid? actorId,
        [FromQuery] string? action,
        [FromQuery] string? targetType,
        [FromQuery] Guid? targetId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (!await CanViewAuditLogAsync(cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);
        var query = ApplyFilters(_dbContext.AuditEvents.AsQueryable(), actorId, action, targetType, targetId, from, to);

        // Newest first; the cursor is a (Timestamp, Sequence) position, so "next" = strictly older.
        //
        // The tiebreak is the hash chain's own Sequence, NOT the row Id (issue #478). Id is a random Guid, so
        // same-instant events came back in an arbitrary order that differed on every read — invisible in
        // production, where timestamps rarely collide, and glaring under the manual capture's frozen demo clock,
        // where EVERY event shares one instant and the audit screenshot reshuffled itself on every run. Sequence
        // is the order the events were actually appended in (ADR 0321 makes it authoritative for the chain), so
        // ordering by it is both stable and more truthful than ordering by when they happen to have been stamped.
        if (Cursor.TryDecodeSequence(cursor, out var cursorTimestamp, out var cursorSequence))
        {
            query = query.Where(e => e.Timestamp < cursorTimestamp || (e.Timestamp == cursorTimestamp && e.Sequence < cursorSequence));
        }

        var fetched = await query
            .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Sequence)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        // The log's own retention policy — read and set at the same address. An action ON this collection, so
        // it is a rel here rather than at the root, which is the rule RootController states (issue #416).
        var links = new List<Link>
        {
            new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET"),
            new("retention", Url.Action(nameof(GetRetention))!, "GET"),
            // The rest of what can be done TO this log, advertised by the log itself rather than composed by
            // each client: take a copy, drop what is past retention, and prove it has not been tampered with.
            new("export", Url.Action(nameof(Export))!, "GET"),
            new("purge", Url.Action(nameof(Purge))!, "POST"),
            new("verify", Url.Action(nameof(Verify))!, "GET"),
            new("worm-verify", Url.Action(nameof(WormVerify))!, "GET"),
        };
        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].Timestamp, page[^1].Sequence);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new AuditEventsListResource
        {
            Events = page.Select(BuildResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await CanViewAuditLogAsync(cancellationToken) ? NoContent() : Forbid();

    // Verifies the caller's tenant hash chain (ADR "Audit trail hash chain") — walks it in Sequence order,
    // recomputes each link, and reports the first break. Safe/idempotent, so a GET. Gated on CanViewAuditLog.
    [HttpGet("verify")]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken)
    {
        if (!await CanViewAuditLogAsync(cancellationToken))
        {
            return Forbid();
        }

        var result = await _chainVerifier.VerifyAsync(cancellationToken);
        return Ok(new AuditChainVerificationResource
        {
            Valid = result.Valid,
            CheckedCount = result.CheckedCount,
            BrokenAtSequence = result.BrokenAtSequence,
            Links = [new Link("self", Url.Action(nameof(Verify))!, "GET")],
        });
    }

    [HttpHead("verify")]
    public async Task<IActionResult> VerifyHead(CancellationToken cancellationToken) =>
        await CanViewAuditLogAsync(cancellationToken) ? NoContent() : Forbid();

    // Verifies the sealed WORM segments against the DB (ADR "Audit WORM segment verify"): the immutable segments
    // catch a DB tamper (even a full re-chain) that the DB chain check can't. Safe/idempotent, so a GET. Gated on
    // CanViewAuditLog.
    [HttpGet("worm-verify")]
    public async Task<IActionResult> WormVerify(CancellationToken cancellationToken)
    {
        if (!await CanViewAuditLogAsync(cancellationToken))
        {
            return Forbid();
        }

        var result = await _wormVerifier.VerifyAsync(cancellationToken);
        return Ok(new AuditWormVerificationResource
        {
            Valid = result.Valid,
            SegmentCount = result.SegmentCount,
            CheckedCount = result.CheckedCount,
            BrokenAtSequence = result.BrokenAtSequence,
            Reason = result.Reason,
            Links = [new Link("self", Url.Action(nameof(WormVerify))!, "GET")],
        });
    }

    [HttpHead("worm-verify")]
    public async Task<IActionResult> WormVerifyHead(CancellationToken cancellationToken) =>
        await CanViewAuditLogAsync(cancellationToken) ? NoContent() : Forbid();

    // The tenant's audit retention window + purge state (ADR "Audit trail retention and purge"). Reading is
    // gated on CanViewAuditLog (viewing is sensitive); setting it is a tenant-admin governance action.
    [HttpGet("retention")]
    public async Task<IActionResult> GetRetention(CancellationToken cancellationToken)
    {
        if (!await CanViewAuditLogAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.AuditRetentionDays, t.AuditChainStartSequence, t.AuditLastPurgedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        return Ok(new RetentionResource
        {
            RetentionDays = tenant.AuditRetentionDays,
            ChainStartSequence = tenant.AuditChainStartSequence,
            LastPurgedAt = tenant.AuditLastPurgedAt,
            Links = [new Link("self", Url.Action(nameof(GetRetention))!, "GET")],
        });
    }

    [HttpHead("retention")]
    public async Task<IActionResult> GetRetentionHead(CancellationToken cancellationToken) =>
        await CanViewAuditLogAsync(cancellationToken) ? NoContent() : Forbid();

    [HttpPut("retention")]
    public async Task<IActionResult> SetRetention([FromBody] SetRetentionRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        if (request.RetentionDays < 0)
        {
            throw new InvalidRetentionDaysException();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            return NotFound();
        }

        tenant.AuditRetentionDays = request.RetentionDays;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new RetentionResource
        {
            RetentionDays = tenant.AuditRetentionDays,
            ChainStartSequence = tenant.AuditChainStartSequence,
            LastPurgedAt = tenant.AuditLastPurgedAt,
            Links = [new Link("self", Url.Action(nameof(GetRetention))!, "GET")],
        });
    }

    // Manually purge the caller's tenant's aged audit events now (per its retention window). A tenant-admin
    // governance action; runs the same AuditRetentionService the background worker uses. A POST action
    // sub-resource, since it's a state change, not a create/replace.
    [HttpPost("purge")]
    public async Task<IActionResult> Purge(CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var purged = await _retentionService.PurgeAsync(tenantId, cancellationToken);
        var startSequence = await _dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.AuditChainStartSequence)
            .SingleAsync(cancellationToken);

        return Ok(new PurgeResource
        {
            PurgedCount = purged,
            ChainStartSequence = startSequence,
            Links = [new Link("self", Url.Action(nameof(Purge))!, "POST")],
        });
    }

    // Streams the caller's tenant audit log (respecting the same filters as the list) as NDJSON — one event
    // per line, oldest-first (chronological, so a SIEM ingests events in order and the hash chain reads from
    // the retained start). Each line carries Sequence + Hash so the exported feed is independently verifiable.
    // See ADR "Audit trail export (NDJSON)". Gated on CanViewAuditLog, like the list.
    // Lists the immutable WORM segments sealed for the tenant (ADR "Audit-log WORM") — each a gap-free Sequence
    // range archived to object storage under Object Lock. Read via ListObjects on the tenant's audit-worm prefix;
    // the locked-until instant comes from the object's lock status. CanViewAuditLog-gated (viewing is sensitive).
    [HttpGet("worm-segments")]
    public async Task<IActionResult> WormSegments(CancellationToken cancellationToken)
    {
        if (!await CanViewAuditLogAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var prefix = $"tenants/{tenantId}/{SimplArchive.Infrastructure.Audit.AuditWormArchiver.Prefix}/";
        var objects = await _objectStorage.ListObjectsAsync(prefix, cancellationToken);

        var segments = new List<WormSegmentResource>();
        foreach (var obj in objects.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            var name = obj.Key[prefix.Length..];
            if (!name.EndsWith(".ndjson"))
            {
                continue;
            }

            var parts = name[..^".ndjson".Length].Split('-');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var fromSeq) || !long.TryParse(parts[1], out var toSeq))
            {
                continue; // not a range segment
            }

            var lockStatus = await _objectStorage.GetLockStatusAsync(obj.Key, cancellationToken);
            segments.Add(new WormSegmentResource
            {
                FromSequence = fromSeq,
                ToSequence = toSeq,
                ObjectKey = obj.Key,
                SizeBytes = obj.Size,
                SealedAt = obj.LastModified,
                LockedUntil = lockStatus.RetainUntil,
            });
        }

        return Ok(new WormSegmentListResource
        {
            Segments = segments,
            Links = [new Link("self", "/api/audit-events/worm-segments", "GET")],
        });
    }

    [HttpHead("worm-segments")]
    public async Task<IActionResult> WormSegmentsHead(CancellationToken cancellationToken) =>
        await CanViewAuditLogAsync(cancellationToken) ? NoContent() : Forbid();

    public class WormSegmentListResource : HypermediaResource
    {
        public List<WormSegmentResource> Segments { get; set; } = [];
    }

    public class WormSegmentResource
    {
        public long FromSequence { get; set; }
        public long ToSequence { get; set; }
        public string ObjectKey { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTimeOffset SealedAt { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] Guid? actorId,
        [FromQuery] string? action,
        [FromQuery] string? targetType,
        [FromQuery] Guid? targetId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (!await CanViewAuditLogAsync(cancellationToken))
        {
            return Forbid();
        }

        var query = ApplyFilters(_dbContext.AuditEvents.AsQueryable(), actorId, action, targetType, targetId, from, to)
            .OrderBy(e => e.Sequence);

        // Written directly to the body (streamed, not buffered) — application/x-ndjson isn't rewritten by
        // VersionedContentTypeMiddleware (it only touches application/json / application/xml).
        Response.ContentType = "application/x-ndjson";
        Response.Headers.ContentDisposition = $"attachment; filename=\"audit-export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.ndjson\"";

        await foreach (var e in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var line = JsonSerializer.Serialize(new AuditExportLine
            {
                Sequence = e.Sequence,
                Hash = e.Hash,
                Timestamp = e.Timestamp,
                ActorType = e.ActorType.ToString(),
                ActorId = e.ActorId,
                ActorName = e.ActorName,
                Action = e.Action,
                TargetType = e.TargetType,
                TargetId = e.TargetId,
                TargetName = e.TargetName,
                Details = e.Details,
            }, ExportJsonOptions);

            await Response.WriteAsync(line + "\n", cancellationToken);
        }

        return new EmptyResult();
    }

    [HttpHead("export")]
    public async Task<IActionResult> ExportHead(CancellationToken cancellationToken) =>
        await CanViewAuditLogAsync(cancellationToken) ? NoContent() : Forbid();

    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web);

    // One NDJSON export line (ADR "Audit trail export"). Sequence + Hash carry the tamper-evidence chain so the
    // exported feed can be re-verified externally.
    private sealed class AuditExportLine
    {
        public long Sequence { get; set; }
        public string Hash { get; set; } = "";
        public DateTimeOffset Timestamp { get; set; }
        public string ActorType { get; set; } = "";
        public Guid ActorId { get; set; }
        public string ActorName { get; set; } = "";
        public string Action { get; set; } = "";
        public string? TargetType { get; set; }
        public Guid? TargetId { get; set; }
        public string? TargetName { get; set; }
        public string? Details { get; set; }
    }

    private static IQueryable<AuditEvent> ApplyFilters(
        IQueryable<AuditEvent> query, Guid? actorId, string? action, string? targetType, Guid? targetId, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (actorId is { } a)
        {
            query = query.Where(e => e.ActorId == a);
        }

        if (!string.IsNullOrEmpty(action))
        {
            query = query.Where(e => e.Action == action);
        }

        if (!string.IsNullOrEmpty(targetType))
        {
            query = query.Where(e => e.TargetType == targetType);
        }

        if (targetId is { } t)
        {
            query = query.Where(e => e.TargetId == t);
        }

        if (from is { } f)
        {
            query = query.Where(e => e.Timestamp >= f);
        }

        if (to is { } upper)
        {
            query = query.Where(e => e.Timestamp <= upper);
        }

        return query;
    }

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;

    private static AuditEventResource BuildResource(AuditEvent e) => new()
    {
        Id = e.Id,
        Timestamp = e.Timestamp,
        ActorType = e.ActorType.ToString(),
        ActorId = e.ActorId,
        ActorName = e.ActorName,
        Action = e.Action,
        TargetType = e.TargetType,
        TargetId = e.TargetId,
        TargetName = e.TargetName,
        Details = e.Details,
    };

    private async Task<bool> CanViewAuditLogAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanViewAuditLog)
                .SingleAsync(cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanViewAuditLog;
        }

        return false;
    }
}
