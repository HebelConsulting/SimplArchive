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
/// A booking is three things made together: the <b>Room booking document</b> (a child of the room — the
/// room is a folder of its bookings, so rights flow down the normal way), the <b>appointment</b> in the
/// room's one <c>Schedule</c> calendar (the CalDAV-subscribable projection), and the
/// <see cref="ResourceBooking"/> row that carries the authoritative slot. The row and the booking document
/// are saved FIRST, so the no-overlap invariant refuses a taken slot before any object-storage write.
/// </para>
/// <para>
/// Cancelling keeps history: the row goes <see cref="BookingStatus.Cancelled"/>, the booking document
/// stays as the record, and the appointment is soft-deleted so every subscribed calendar clears.
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

        // 1. The booking document + the claim row, FIRST and in one save: the DbContext's no-overlap
        //    invariant refuses a taken slot here, before anything touches object storage.
        var bookingDocument = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = room.TenantId,
            ParentId = room.Id,
            Name = await UniqueNameAsync(room.Id, $"Booking {startsAt:yyyy-MM-dd HH:mm}–{endsAt:HH:mm}", cancellationToken),
            MaskVersionId = await CurrentMaskVersionIdAsync(WellKnownMaskIds.RoomBooking, cancellationToken),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = now,
            StorageFolderId = Guid.NewGuid(),
        };
        _dbContext.Documents.Add(bookingDocument);

        if (!string.IsNullOrWhiteSpace(request.Purpose))
        {
            var purposeFieldId = await _dbContext.FieldDefinitions
                .Where(f => f.MaskVersionId == bookingDocument.MaskVersionId && f.Name == "Purpose")
                .Select(f => (Guid?)f.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (purposeFieldId is { } fieldId)
            {
                _dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = room.TenantId,
                    DocumentId = bookingDocument.Id,
                    FieldDefinitionId = fieldId,
                    Value = request.Purpose.Trim(),
                });
            }
        }

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
        catch (BookingInvariantException ex) when (ex.Message.Contains("overlaps"))
        {
            // Caught SPECIFICALLY: left to a blanket InvalidOperationException catch, a slot conflict
            // reports as whatever that catch assumes (the blanket-catch-false-cause lesson).
            throw new BookingSlotConflictException(ex.Message);
        }

        // 2. The projection: the room's one Schedule calendar (created on first booking — the
        //    child-cardinality rule caps it at one), and the appointment inside it. AppointmentDocumentId
        //    stays null if anything past the save fails — the slot is held either way, and the column is
        //    nullable by design (ADR 0503's precedent).
        var appointmentId = await FileAppointmentAsync(room, bookingDocument.Name, startsAt, endsAt, userId, serviceAccountId, cancellationToken);
        booking.AppointmentDocumentId = appointmentId;
        await _dbContext.SaveChangesAsync(cancellationToken);

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

        // The projection follows the claim: soft-delete the appointment so every subscribed calendar
        // clears; the booking DOCUMENT stays — cancelled is history, not erasure.
        if (booking.AppointmentDocumentId is { } appointmentId)
        {
            var appointment = await _dbContext.Documents
                .FirstOrDefaultAsync(d => d.Id == appointmentId, cancellationToken);
            if (appointment is not null)
            {
                appointment.DeletedAt = DateTimeOffset.UtcNow;
            }
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

    private async Task<Guid?> FileAppointmentAsync(
        Document room, string summary, DateTimeOffset startsAt, DateTimeOffset endsAt,
        Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        // The room's Schedule calendar, created on first booking. By mask, not by name (a renamed calendar
        // still counts), and the OLDEST one deterministically — cardinality is deliberately uncapped (a
        // capacity rule would derive Calendar into ImmutableStructuralMasks against the decided boundary),
        // so a second calendar is harmless clutter this ordering simply ignores.
        var calendarMaskVersionId = await CurrentMaskVersionIdAsync(WellKnownMaskIds.Calendar, cancellationToken);
        var calendar = await _dbContext.Documents
            .Where(d => d.ParentId == room.Id)
            .Join(_dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (d, v) => new { d, v.MaskId })
            .Where(x => x.MaskId == WellKnownMaskIds.Calendar)
            .Select(x => x.d)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (calendar is null)
        {
            calendar = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = room.TenantId,
                ParentId = room.Id,
                Name = "Schedule",
                MaskVersionId = calendarMaskVersionId,
                CreatedByUserId = userId,
                CreatedByServiceAccountId = serviceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.Documents.Add(calendar);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var uid = Guid.NewGuid().ToString();
        var blob = _appointments.Merge(null, DocumentAppointmentController.FromResource(new DocumentAppointmentController.AppointmentResource
        {
            Summary = summary,
            Start = startsAt.UtcDateTime,
            End = endsAt.UtcDateTime,
            StartTimeZoneId = "UTC",
            EndTimeZoneId = "UTC",
            Location = room.Name,
        }), uid);

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var storageFolderId = Guid.NewGuid();
        var appointment = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = room.TenantId,
            ParentId = calendar.Id,
            Name = await UniqueNameAsync(calendar.Id, summary, cancellationToken),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = serviceAccountId,
            CreatedAt = now,
            StorageFolderId = storageFolderId,
            // Maskless: the finalizer classifies .ics as Appointment (the TypedItemsController reasoning).
        };
        _dbContext.Documents.Add(appointment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var objectKey = ObjectKeyBuilder.Build(room.TenantId, now, storageFolderId, versionId, ".ics");
        await _storage.PutObjectAsync(objectKey, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(blob)), "text/calendar", cancellationToken);

        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = appointment.Id,
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

        return appointment.Id;
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
