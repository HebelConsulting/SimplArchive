using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Booking;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// A bookable resource's bookings — the inventory-booking primitive's HTTP edge (ADR 0735; endpoints
/// interview 2026-09-03).
/// </summary>
/// <remarks>
/// <para>
/// One rel (<c>bookings</c>), the method says the action (ADR 0719): <c>GET</c> lists the resource's
/// bookings, <c>POST</c> on the same address books a slot. Seeing a shared resource is what lets a caller
/// book it — an industry module tightens that through its state machine (ADR 0742), the core does not.
/// </para>
/// <para>
/// A booking is two things made together (ADR 0744): the <b>.ics document</b> in the room's one
/// <c>Schedule</c> — the booking IS the calendar entry, wearing the Room-booking mask, CalDAV-subscribable,
/// rights flowing down from the room the normal way — and the <see cref="ResourceBooking"/> row that
/// carries the authoritative slot. The row and the (still-unclassified) document are saved FIRST, so the
/// no-overlap invariant refuses a taken slot before any object-storage write.
/// </para>
/// <para>
/// Cancelling soft-deletes the .ics so every subscribed calendar clears; the row goes
/// <see cref="BookingStatus.Cancelled"/> and is the durable history, outliving even a purge.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/bookings")]
[Authorize]
public class BookingsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly IObjectStorageClient _storage;
    private readonly IAppointmentComposer _appointments;
    private readonly DocumentFinalizer _finalizer;

    public BookingsController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        IAppointmentComposer appointments,
        DocumentFinalizer finalizer)
    {
        _dbContext = dbContext;
        _access = access;
        _storage = storage;
        _appointments = appointments;
        _finalizer = finalizer;
    }

    // Plain mutable classes, not records — XmlSerializer (ADRs 0189/0190).
    public class BookingResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public DateTimeOffset StartsAt { get; set; }

        public DateTimeOffset EndsAt { get; set; }

        public string Status { get; set; } = string.Empty;

        /// <summary>Who holds the slot — a display name, resolved from the booker.</summary>
        public string BookedBy { get; set; } = string.Empty;

        public string? Purpose { get; set; }

        /// <summary>False when the caller may see but not cancel — the client disables the affordance.</summary>
        public bool CanCancel { get; set; }

        /// <summary>The row's concurrency token, for the cancel's If-Match — the ExternalLinks precedent:
        /// a row-level mutation needs the token to travel WITH the row, or every cancel costs a fetch
        /// (ADR 0557).</summary>
        public string Etag { get; set; } = string.Empty;
    }

    public class BookingListResource : HypermediaResource
    {
        public List<BookingResource> Bookings { get; set; } = [];

        public string? NextCursor { get; set; }

        /// <summary>
        /// True when the caller may book this resource (endpoints interview: seeing it is enough). Beside
        /// the rel rather than instead of it, because listing ROWS will carry it as a capability flag
        /// (ADR 0723) where rels are deliberately not emitted.
        /// </summary>
        public bool CanBook { get; set; }
    }

    public class BookRequest
    {
        public DateTimeOffset StartsAt { get; set; }

        public DateTimeOffset EndsAt { get; set; }

        public string? Purpose { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (await RequireBookableAsync(documentId, cancellationToken) is not { } room)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanSee)
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.ResourceBookings
            .Where(b => b.ResourceDocumentId == documentId);
        if (Cursor.TryDecode(cursor, out var afterCreatedAt, out var afterId))
        {
            query = query.Where(b => b.CreatedAt > afterCreatedAt
                || (b.CreatedAt == afterCreatedAt && b.Id.CompareTo(afterId) > 0));
        }

        var rows = await query
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var page = rows.Take(pageSize).ToList();
        var (callerUserId, callerServiceAccountId) = _access.GetCallerIdentity();
        var canCancelAny = rights.CanDelete; // a room admin clears anybody's booking

        var resource = new BookingListResource
        {
            NextCursor = rows.Count > pageSize
                ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id)
                : null,
            CanBook = true, // the gate is CanSee, already enforced above; modules tighten later (ADR 0742)
            Links =
            {
                new Link("self", $"/api/documents/{documentId}/bookings", "GET"),
                new Link("resource", $"/api/documents/{documentId}", "GET"),
            },
        };

        foreach (var row in page)
        {
            resource.Bookings.Add(await ToResourceAsync(row, canCancelAny, callerUserId, callerServiceAccountId, cancellationToken));
        }

        return Ok(resource);
    }

    // Its own action — ASP.NET Core does not strip a GET body for HEAD (repo convention).
    [HttpHead]
    public Task<IActionResult> Head(
        Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        List(documentId, cursor, limit, cancellationToken);

    [HttpPost]
    public async Task<IActionResult> Book(Guid documentId, [FromBody] BookRequest request, CancellationToken cancellationToken)
    {
        if (await RequireBookableAsync(documentId, cancellationToken) is not { } room)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanSee)
        {
            return Forbid();
        }

        var startsAt = request.StartsAt.ToUniversalTime();
        var endsAt = request.EndsAt.ToUniversalTime();
        if (startsAt >= endsAt)
        {
            throw new BookingSlotInvalidException(
                $"The slot's start {startsAt:u} must precede its end {endsAt:u}.");
        }

        var (userId, serviceAccountId) = _access.GetCallerIdentity();
        var now = DateTimeOffset.UtcNow;

        var schedule = await EnsureScheduleAsync(room, userId, serviceAccountId, cancellationToken);

        // 1. The (still-unclassified) booking document + the claim row, FIRST and in one save: the
        //    DbContext's no-overlap invariant refuses a taken slot here, before anything touches object
        //    storage. Maskless deliberately — the finalizer classifies an .ics in a Schedule as Room
        //    booking and fills its fields (ADR 0744), the same path a CalDAV PUT takes.
        var bookingDocument = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = room.TenantId,
            ParentId = schedule.Id,
            Name = await UniqueNameAsync(schedule.Id, $"Booking {startsAt:yyyy-MM-dd HH:mm}–{endsAt:HH:mm}", cancellationToken),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = now,
            StorageFolderId = Guid.NewGuid(),
        };
        _dbContext.Documents.Add(bookingDocument);

        var booking = new ResourceBooking
        {
            Id = Guid.NewGuid(),
            TenantId = room.TenantId,
            ResourceDocumentId = room.Id,
            BookingDocumentId = bookingDocument.Id,
            StartsAtUtc = startsAt,
            EndsAtUtc = endsAt,
            Status = BookingStatus.Active,
            BookedByUserId = userId,
            BookedByServiceAccountId = serviceAccountId,
            CreatedAt = now,
        };
        _dbContext.ResourceBookings.Add(booking);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (BookingInvariantException ex) when (ex.Kind == BookingInvariantKind.SlotTaken)
        {
            // Caught SPECIFICALLY — by the invariant's own Kind, not by matching message text — because
            // left to a blanket InvalidOperationException catch, a slot conflict reports as whatever that
            // catch assumes (the blanket-catch-false-cause lesson).
            throw new BookingSlotConflictException(ex.Message);
        }

        // 2. The bytes: the booking IS the .ics (ADR 0744) — the slot, the room as LOCATION, the purpose
        //    as DESCRIPTION — finalized through the same classifier every write path uses, which stamps
        //    the Room-booking mask, indexes the fields, and adopts the row created above. If anything past
        //    the first save fails, the slot is held and the document is an unclassified husk the cancel
        //    path still clears — the same failure mode the two-document shape had.
        var uid = Guid.NewGuid().ToString();
        var blob = _appointments.Merge(null, DocumentAppointmentController.FromResource(new DocumentAppointmentController.AppointmentResource
        {
            Summary = bookingDocument.Name,
            Start = startsAt.UtcDateTime,
            End = endsAt.UtcDateTime,
            StartTimeZoneId = "UTC",
            EndTimeZoneId = "UTC",
            Location = room.Name,
            Description = string.IsNullOrWhiteSpace(request.Purpose) ? null : request.Purpose.Trim(),
        }), uid);

        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(room.TenantId, now, bookingDocument.StorageFolderId, versionId, ".ics");
        await _storage.PutObjectAsync(objectKey, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(blob)), "text/calendar", cancellationToken);

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = bookingDocument.Id,
            TenantId = room.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            // Exactly one of the two — the caller may be a service account (CK_DocumentVersions_ExactlyOneCreator).
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _finalizer.FinalizeAsync(version, cancellationToken);

        var created = await ToResourceAsync(booking, rights.CanDelete, userId, serviceAccountId, cancellationToken);
        Response.Headers.ETag = $"\"{booking.ConcurrencyToken}\"";
        return Created($"/api/documents/{documentId}/bookings/{booking.Id}", created);
    }

    [HttpDelete("{bookingId:guid}")]
    public async Task<IActionResult> Cancel(Guid documentId, Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await _dbContext.ResourceBookings
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.ResourceDocumentId == documentId, cancellationToken);
        if (booking is null)
        {
            return NotFound();
        }

        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        var (userId, serviceAccountId) = _access.GetCallerIdentity();
        var isBooker = (userId is not null && booking.BookedByUserId == userId)
            || (serviceAccountId is not null && booking.BookedByServiceAccountId == serviceAccountId);
        if (!isBooker && !rights.CanDelete)
        {
            return Forbid();
        }

        if (Request.Headers.IfMatch.Count == 0)
        {
            throw new IfMatchRequiredException();
        }

        if (!Guid.TryParse(Request.Headers.IfMatch.ToString().Trim('"'), out var expectedToken))
        {
            throw EtagMismatchException.ForBooking();
        }

        _dbContext.Entry(booking).Property(b => b.ConcurrencyToken).OriginalValue = expectedToken;
        booking.Status = BookingStatus.Cancelled;

        // The booking IS the .ics (ADR 0744): soft-delete it, so every subscribed calendar clears and the
        // recycle bin holds the document until purge. The row — flipped here and, in agreement, by the
        // SaveChanges sync that watches booking documents — is the durable history.
        var bookingDocument = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.Id == booking.BookingDocumentId, cancellationToken);
        if (bookingDocument is not null)
        {
            bookingDocument.DeletedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForBooking();
        }

        return NoContent();
    }

    /// <summary>The document, if it exists and its mask declares it bookable (ADR 0735).</summary>
    /// <remarks>
    /// 404 rather than the <see cref="ResourceNotBookableException"/> 409 for a document that exists but is
    /// not bookable would hide a real distinction; the 409 is thrown by <see cref="Book"/>'s save path via
    /// the DbContext invariant only for races. Here, a non-bookable target simply has no bookings surface —
    /// the rel was never emitted (ADR 0543), so NotFound matches what the link map said.
    /// </remarks>
    private async Task<Document?> RequireBookableAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var room = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (room?.MaskVersionId is not { } maskVersionId)
        {
            return null;
        }

        var isBookable = await _dbContext.MaskVersions
            .Where(v => v.Id == maskVersionId)
            .Join(_dbContext.Masks, v => new { v.TenantId, Id = v.MaskId }, m => new { m.TenantId, m.Id }, (v, m) => m.IsBookable)
            .SingleOrDefaultAsync(cancellationToken);

        return isBookable ? room : null;
    }

    /// <summary>The room's one Schedule (ADR 0744), created on first booking.</summary>
    /// <remarks>
    /// By mask, not by name (a renamed schedule still counts); the cardinality rule caps a room at one, so
    /// there is nothing to order between. An old-shape room may still hold a plain Calendar named
    /// "Schedule" — the unique-name fallback steps around it, and the leftover is inert (ADR 0744: no
    /// dual-shape support, no heal).
    /// </remarks>
    private async Task<Document> EnsureScheduleAsync(
        Document room, Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        var schedule = await _dbContext.Documents
            .Where(d => d.ParentId == room.Id)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { d, v.MaskId })
            .Where(x => x.MaskId == WellKnownMaskIds.Schedule)
            .Select(x => x.d)
            .FirstOrDefaultAsync(cancellationToken);
        if (schedule is not null)
        {
            return schedule;
        }

        schedule = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = room.TenantId,
            ParentId = room.Id,
            Name = await UniqueNameAsync(room.Id, "Schedule", cancellationToken),
            MaskVersionId = await CurrentMaskVersionIdAsync(WellKnownMaskIds.Schedule, cancellationToken),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.Documents.Add(schedule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return schedule;
    }

    private async Task<BookingResource> ToResourceAsync(
        ResourceBooking row, bool canCancelAny, Guid? callerUserId, Guid? callerServiceAccountId, CancellationToken cancellationToken)
    {
        var bookedBy = row.BookedByUserId is { } bookerId
            ? await _dbContext.Users.Where(u => u.Id == bookerId).Select(u => u.DisplayName).FirstOrDefaultAsync(cancellationToken)
            : await _dbContext.ServiceAccounts.Where(s => s.Id == row.BookedByServiceAccountId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken);

        var purpose = await _dbContext.FieldValues
            .Where(v => v.DocumentId == row.BookingDocumentId)
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { v.Value, f.Name })
            .Where(x => x.Name == "Purpose")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);

        var isBooker = (callerUserId is not null && row.BookedByUserId == callerUserId)
            || (callerServiceAccountId is not null && row.BookedByServiceAccountId == callerServiceAccountId);

        var resource = new BookingResource
        {
            Id = row.Id,
            StartsAt = row.StartsAtUtc,
            EndsAt = row.EndsAtUtc,
            Status = row.Status.ToString(),
            BookedBy = bookedBy ?? string.Empty,
            Purpose = purpose,
            CanCancel = row.Status == BookingStatus.Active && (isBooker || canCancelAny),
            Etag = row.ConcurrencyToken.ToString(),
            Links =
            {
                // The booking's own document — its address came with this row, so following it costs
                // nothing extra (ADR 0557).
                new Link("document", $"/api/documents/{row.BookingDocumentId}", "GET"),
            },
        };
        if (resource.CanCancel)
        {
            resource.Links.Add(new Link("cancel", $"/api/documents/{row.ResourceDocumentId}/bookings/{row.Id}", "DELETE"));
        }

        return resource;
    }

    private async Task<Guid?> CurrentMaskVersionIdAsync(Guid maskId, CancellationToken cancellationToken) =>
        await _dbContext.MaskVersions
            .Where(v => v.MaskId == maskId && v.IsCurrent)
            .Select(v => (Guid?)v.Id)
            .SingleOrDefaultAsync(cancellationToken);

    // The sibling-name invariant refuses duplicates at SaveChanges; picking a free name here turns what
    // would be a 409 into the ordinary outcome (the TypedItemsController pattern).
    private async Task<string> UniqueNameAsync(Guid parentId, string baseName, CancellationToken cancellationToken)
    {
        var siblings = await _dbContext.Documents
            .Where(d => d.ParentId == parentId)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        if (!siblings.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!siblings.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }
}
