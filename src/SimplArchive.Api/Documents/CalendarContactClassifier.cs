using System.Globalization;
using FolkerKinzel.VCards;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Booking;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Auto-classification of a stored <c>.vcf</c>/<c>.ics</c> into the Contact / Calendar well-known masks
/// (#564, ADR 0619) — the CalDAV/CardDAV twin of the finalizer's email classification, in its own class
/// because <see cref="DocumentFinalizer"/> is already at the size the standing rule guards.
/// </summary>
/// <remarks>
/// It runs on ANY upload of such a file, not only on a DAV write: a contact dragged into a Addressbook
/// through the workbench must end up indistinguishable from one a phone synced there, and the typed-folder
/// containment invariant would otherwise refuse it (the document would wear Basic Entry, not Contact).
/// Parsing is best-effort — an unparseable file falls through to the finalizer's default mask rather than
/// failing the upload, exactly as a malformed .eml does.
/// </remarks>
public sealed class CalendarContactClassifier
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly IContactCardComposer _contacts;
    private readonly ILogger<CalendarContactClassifier> _logger;
    private readonly IAuditRecorder _audit;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly INotificationService _notifications;

    public CalendarContactClassifier(
        SimplArchiveDbContext dbContext, IObjectStorageClient objectStorageClient,
        IContactCardComposer contacts, ILogger<CalendarContactClassifier> logger, IAuditRecorder audit,
        IUserSystemRightsResolver userSystemRights, INotificationService notifications)
    {
        _dbContext = dbContext;
        _objectStorageClient = objectStorageClient;
        _contacts = contacts;
        _logger = logger;
        _audit = audit;
        _userSystemRights = userSystemRights;
        _notifications = notifications;
    }

    /// <summary>The extensions this classifier owns.</summary>
    public static bool Handles(string extension) => extension is ".vcf" or ".ics";

    /// <summary>
    /// Classifies the document behind <paramref name="version"/> when it is a vCard/iCalendar, returning
    /// whether it did. The caller has already established the document is still unclassified and that its
    /// destination admits <paramref name="itemMaskId"/> — which is also what SAYS the mask: an .ics is an
    /// Appointment in a Calendar and a Booking in a Schedule (ADR 0744, the Note/eMail precedent —
    /// told apart by where it is filed, not by its bytes).
    /// </summary>
    public async Task<bool> TryClassifyAsync(Document document, DocumentVersion version, Guid itemMaskId, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(version.ObjectKey).ToLowerInvariant();
        if (!Handles(extension))
        {
            return false;
        }

        var maskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, document.TenantId, itemMaskId, cancellationToken);
        if (maskVersionId is null)
        {
            // The mask is not seeded for this tenant — leave the document unclassified rather than half-typed.
            _logger.LogWarning("Mask {MaskId} is not seeded for tenant {TenantId}; leaving {DocumentId} unclassified",
                itemMaskId, document.TenantId, document.Id);
            return false;
        }

        if (await ReadContentAsync(version, cancellationToken) is not { } content)
        {
            return false;
        }

        return extension switch
        {
            ".vcf" => await ClassifyContactAsync(document, version, content, maskVersionId.Value, cancellationToken),
            ".ics" => await ClassifyCalendarAsync(document, version, content, itemMaskId, maskVersionId.Value, cancellationToken),
            _ => false,
        };
    }

    /// <summary>
    /// Re-extracts an ALREADY-classified item's indexed fields from a new version — the half every edit
    /// path was missing (ADR 0744): before this, `AutoClassifyAsync` skipped classified documents and
    /// nothing else re-read the bytes, so an edited appointment kept its original Name/UID/Start/End in
    /// every listing. For a Booking the same pass moves the claim row's slot, which is what makes an
    /// edit a REBOOKING — refused through the overlap invariant like any other booking write.
    /// </summary>
    /// <remarks>Returns false when the document is not a collection-kind item of a handled extension.</remarks>
    public async Task<bool> TryRefreshAsync(Document document, DocumentVersion version, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(version.ObjectKey).ToLowerInvariant();
        if (!Handles(extension) || document.MaskVersionId is not { } maskVersionId)
        {
            return false;
        }

        var maskId = await _dbContext.MaskVersions
            .Where(v => v.Id == maskVersionId)
            .Select(v => (Guid?)v.MaskId)
            .FirstOrDefaultAsync(cancellationToken);

        // The refreshable set is DERIVED from the collection kinds, not restated — a module that declares
        // a new kind (ADR 0744's recipe) gets edit-refresh for free instead of silently stale fields.
        if (maskId is not { } mask || !DavCollectionKinds.All.Any(k => k.ItemMaskId == mask && k.Extension == extension))
        {
            return false;
        }

        if (await ReadContentAsync(version, cancellationToken) is not { } content)
        {
            return false;
        }

        // The document's OWN mask version, deliberately: refreshing must rewrite the values the document
        // already has, and its field definitions belong to the version it wears — restamping to the
        // current mask version here would strand the old values under definitions nothing reads.
        return extension switch
        {
            ".vcf" => await ClassifyContactAsync(document, version, content, maskVersionId, cancellationToken),
            ".ics" => await ClassifyCalendarAsync(document, version, content, mask, maskVersionId, cancellationToken),
            _ => false,
        };
    }

    private async Task<string?> ReadContentAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _objectStorageClient.GetObjectAsync(version.ObjectKey, cancellationToken);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not read {ObjectKey} for calendar/contact classification", version.ObjectKey);
            return null;
        }
    }

    private async Task<bool> ClassifyContactAsync(Document document, DocumentVersion version, string content, Guid maskVersionId, CancellationToken cancellationToken)
    {
        FolkerKinzel.VCards.VCard? card;
        try
        {
            card = Vcf.Parse(content).FirstOrDefault();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Unparseable vCard in {ObjectKey}", version.ObjectKey);
            return false;
        }

        if (card is null)
        {
            return false;
        }

        // A vCard's UID is optional in the wild; without one the document id stands in, so the correlation key
        // a later DAV PUT matches on always exists (a client that supplies no UID simply never matches, which
        // is the correct outcome — it is asking for a new item every time).
        var contactId = card.ContactID?.Value;
        var uid = Nonempty(contactId?.String)
            ?? Nonempty(contactId?.Guid?.ToString())
            ?? Nonempty(contactId?.Uri?.ToString())
            ?? document.Id.ToString();
        var fullName = Nonempty(card.DisplayNames?.FirstOrDefault()?.Value)
            ?? Nonempty(card.NameViews?.FirstOrDefault()?.Value?.ToString());

        var values = new List<(string Field, string? Value)>
        {
            ("Contact UID", uid),
            ("Full name", fullName),
            ("Email", Nonempty(card.EMails?.FirstOrDefault()?.Value)),
            ("Phone", Nonempty(card.Phones?.FirstOrDefault()?.Value)),
            ("Organization", Nonempty(card.Organizations?.FirstOrDefault()?.Value?.Name)),
            // Its media type, not the picture: index data is queried, listed and exported, and a base64 image
            // in it would be all three. The bytes stay in the card and are served from their own address.
            ("Photo", _contacts.ReadPhoto(content)?.ContentType),
        };

        await ApplyAsync(document, maskVersionId, values, fullName, cancellationToken);
        return true;
    }

    private async Task<bool> ClassifyCalendarAsync(
        Document document, DocumentVersion version, string content, Guid maskId, Guid maskVersionId, CancellationToken cancellationToken)
    {
        Ical.Net.Calendar? calendar;
        try
        {
            calendar = Ical.Net.Calendar.Load(content);
        }
        catch (Exception parseFailure)
        {
            _logger.LogDebug(parseFailure, "Unparseable iCalendar in {ObjectKey}", version.ObjectKey);
            return false;
        }

        var occurrence = calendar?.Events.FirstOrDefault();
        if (occurrence is null)
        {
            return false;
        }

        // RRULE stays opaque in the stored .ics (the epic's decision — no server-side expansion), so the
        // indexed Start/End are the FIRST occurrence's: enough to find and list the item, never authoritative
        // for a recurring series. The .ics itself is what a client renders.
        var start = occurrence.DtStart?.Value;
        var end = occurrence.DtEnd?.Value;

        PendingBookingAudit? pendingAudit = null;
        var values = new List<(string Field, string? Value)>
        {
            ("Event UID", Nonempty(occurrence.Uid) ?? document.Id.ToString()),
            ("Start", Stamp(occurrence.DtStart)),
            ("End", Stamp(occurrence.DtEnd)),
            ("Location", Nonempty(occurrence.Location)),
        };

        if (maskId == WellKnownMaskIds.Booking)
        {
            // The booking IS the .ics (ADR 0744): the same pass that indexes the fields moves the claim.
            // Deliberately BEFORE ApplyAsync, so the row rides the same save as the fields and the
            // DbContext's overlap invariant judges them together — refusing a conflicting write on every
            // path (a CalDAV PUT, a drop-upload, the booking endpoint) at the one door they all use.
            values.Add(("Purpose", Nonempty(occurrence.Description)));
            pendingAudit = await UpsertBookingRowAsync(document, version, occurrence, cancellationToken);
        }
        else if (maskId == WellKnownMaskIds.MaintenanceBlock)
        {
            // A block is written the same way a booking is (ADR 0778): the .ics in the resource's Maintenance
            // collection IS the block, and the same pass that indexes the fields moves the row. Same door,
            // same ordering — before ApplyAsync, so the row rides the save the invariants judge.
            values.Add(("Reason", Nonempty(occurrence.Description)));
            pendingAudit = await UpsertBlockRowAsync(document, version, occurrence, cancellationToken);
        }
        else
        {
            // Indexed so a listing can SAY the entry repeats without opening the blob. The rule itself stays
            // opaque — this is the stored text, not an interpretation of it, and nothing here expands a
            // recurrence set. What it buys is honesty in the grid: an entry drawn at its first occurrence and
            // nowhere else is under-reporting the month, and a marker is what stops that being silent.
            // Bookings have no Repeats field at all — a recurring booking is refused above.
            values.Add(("Repeats", Nonempty(RecurrenceRule(occurrence))));
        }

        try
        {
            await ApplyAsync(document, maskVersionId, values, Nonempty(occurrence.Summary), cancellationToken);
        }
        catch (BookingInvariantException e)
        {
            // Translated by FACT (the Kind), never by matching message text — and through the SHARED
            // translation, so this door and the bookings endpoint cannot answer differently for one act.
            throw BookingInvariantTranslation.Translate(e);
        }

        // Recorded only now: the booking survived the save, so the event describes something that happened.
        // Recording before it would have written a trail entry for a booking the very next line refused.
        if (pendingAudit is { } audit)
        {
            await _audit.RecordAsync(audit.Action, "Document", document.Id, document.Name, audit.Details,
                tenantId: document.TenantId, cancellationToken: cancellationToken);

            if (audit.Notice is { } notice)
            {
                await NotifyAffectedAsync(notice, cancellationToken);
            }
        }

        if (start is { } startDate)
        {
            version.DocumentDate = DateOnly.FromDateTime(startDate);
            // A timed appointment carries its start time-of-day; an all-day event (DtStart.HasTime == false)
            // stays date-only (ADR "Optional time on the document date"). Mirrors the source of the date above.
            version.DocumentTime = occurrence.DtStart is { HasTime: true } ? TimeOnly.FromDateTime(startDate) : null;
        }

        return true;
    }

    /// <summary>Creates or moves the <see cref="ResourceBooking"/> claim behind a Schedule's .ics (ADR 0744).</summary>
    /// <remarks>
    /// The row is authoritative for the slot; the indexed Start/End are its projection. A new row's booker
    /// is the version's creator — on a CalDAV PUT that is the authenticated DAV user, on the booking
    /// endpoint the caller, so "who holds the slot" is right on every path.
    /// </remarks>
    private async Task<PendingBookingAudit> UpsertBookingRowAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.RecurrenceRule is not null)
        {
            throw new BookingRecurrenceUnsupportedException();
        }

        if (Instant(occurrence.DtStart) is not { } startsAt)
        {
            throw new BookingSlotInvalidException("The event carries no DTSTART — a booking must claim a slot.");
        }

        // An all-day event's DTEND is already exclusive (the day after); a missing DTEND collapses the
        // slot to zero extent, which the invariant refuses with the extent named.
        var endsAt = Instant(occurrence.DtEnd) ?? startsAt;

        // The room is the Schedule's parent — containment guarantees the shape (a Booking lives only
        // in a Schedule, a Schedule only in a room), so a missing grandparent is a state this code cannot
        // reach through any admitted write.
        var roomId = await _dbContext.Documents
            .Where(d => d.Id == document.ParentId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (roomId is not { } resourceId)
        {
            throw new ResourceNotBookableException(
                $"The Schedule holding document {document.Id} has no parent room to claim a slot on.");
        }

        // EVERY claim of this document, not the first (ADR 0774). Picking an arbitrary one and re-pointing it
        // at the room would reassign a PERSON's claim to the aircraft — and moving one claim's slot while its
        // siblings kept the old one would fail the same-window invariant, so an edit of a multi-claim booking
        // would be refused outright. Unreachable before this slice, because nothing created a second claim.
        var claims = await _dbContext.ResourceBookings
            .Where(b => b.BookingDocumentId == document.Id)
            .ToListAsync(cancellationToken);

        var holding = claims.FirstOrDefault(c => c.ResourceDocumentId == resourceId)
            // The holding claim is identified by its RESOURCE. On a move between two Schedules the row still
            // names the old room at this point, and there is exactly one non-attendee claim, so it is that one.
            ?? claims.FirstOrDefault(c => !AttendeeResourceIds(claims, resourceId).Contains(c.ResourceDocumentId));

        // "First booking" is the DOCUMENT'S first version, not "no claim existed yet". The bookings endpoint
        // creates the claim row itself before the finalizer runs, so a claim is already there on that path and
        // every booking made in the app would otherwise be recorded as a change to something that never
        // existed. The version number is the fact that means the same thing on every entrance.
        var wasNew = version.VersionNumber is null or <= 1;
        if (holding is null)
        {
            holding = NewClaim(document, version, resourceId, startsAt, endsAt);
            _dbContext.ResourceBookings.Add(holding);
            claims.Add(holding);
        }

        holding.ResourceDocumentId = resourceId;

        // The slot moves on EVERY claim: they are one event, and the invariant requires them to agree.
        foreach (var claim in claims)
        {
            claim.StartsAtUtc = startsAt.ToUniversalTime();
            claim.EndsAtUtc = endsAt.ToUniversalTime();
        }

        await ReconcileAttendeeClaimsAsync(document, version, occurrence, claims, holding, startsAt, endsAt, cancellationToken);

        // The audit event is BUILT here and RECORDED by the caller, after the save that may still refuse this
        // booking. IAuditRecorder.RecordAsync calls SaveChangesAsync itself, so recording inline would flush
        // the staged claims early — outside the try/catch that translates a slot conflict into a 409 — and a
        // conflicting PUT would answer 500 instead. Which is exactly what it did: the re-entrancy hazard
        // written up for the CANCELLATION path, walked into on the create path.
        var resourceNames = await _dbContext.Documents
            .Where(d => claims.Select(c => c.ResourceDocumentId).Contains(d.Id))
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);

        return new PendingBookingAudit(
            wasNew ? Controllers.AuditActions.BookingCreated : Controllers.AuditActions.BookingChanged,
            $"{startsAt:u}–{endsAt:u} claiming {string.Join(", ", resourceNames.OrderBy(n => n, StringComparer.Ordinal))}");
    }

    /// <summary>Creates or moves the <see cref="ResourceBlock"/> behind a Maintenance collection's .ics (ADR 0778).</summary>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="UpsertBookingRowAsync"/>, and short for the reasons a block is simpler than a
    /// booking: it claims exactly one resource, so there is no attendee expansion and no set of sibling claims
    /// to keep agreeing about the window; and it may overlap anything, so there is nothing to reconcile
    /// against its neighbours.
    /// </para>
    /// <para>
    /// It does NOT touch the bookings it catches. Suspension is derived from this row (#1091) — a booking is
    /// suspended exactly while an Active block of its resource overlaps it — so writing a block is the whole
    /// act, and clearing it is the whole revival. That is what keeps this method out of the re-entrancy trap
    /// the booking path fell into: nothing here writes another document, so nothing re-enters the classifier.
    /// </para>
    /// </remarks>
    private async Task<PendingBookingAudit> UpsertBlockRowAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence, CancellationToken cancellationToken)
    {
        if (occurrence.RecurrenceRule is not null)
        {
            // Same refusal as a booking, for the same reason: the row models ONE window, and a rule the
            // server does not expand would make the stored block and the displayed one disagree.
            throw new BookingRecurrenceUnsupportedException();
        }

        if (Instant(occurrence.DtStart) is not { } startsAt)
        {
            throw new BookingSlotInvalidException("The event carries no DTSTART — a block must name a window.");
        }

        var endsAt = Instant(occurrence.DtEnd) ?? startsAt;

        // The resource is the Maintenance collection's parent — containment guarantees the shape (a block
        // lives only in a Maintenance collection, which lives only on a bookable resource).
        var parentId = await _dbContext.Documents
            .Where(d => d.Id == document.ParentId)
            .Select(d => d.ParentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (parentId is not { } resourceId)
        {
            throw new ResourceNotBookableException(
                $"The Maintenance collection holding document {document.Id} has no parent resource to block.");
        }

        var block = await _dbContext.ResourceBlocks
            .FirstOrDefaultAsync(b => b.BlockDocumentId == document.Id, cancellationToken);

        // CLEARING IS A WRITE, not a delete (ADR 0778). STATUS:CANCELLED in the .ics ends the block, which
        // puts the release back at this single door — the same one that placed it — where the right can be
        // checked and the event recorded. Deleting the document still clears the row as a safety net
        // (SyncBlockDocumentsAsync), but that happens inside SaveChanges, where an audit call would be a
        // nested save; recording per-entrance instead is what ADR 0777 exists to reject.
        var wantsCleared = string.Equals(occurrence.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
        var wasActive = block is { Status: BlockStatus.Active };
        var willBeActive = !wantsCleared;

        await EnsureBlockRightsAsync(version, wasActive, willBeActive, cancellationToken);

        // The bookings this write affects, measured BEFORE the row changes — because suspension is derived,
        // the overlap is only observable while the block's state still says what it said. Afterwards the
        // question is unanswerable, which is exactly why the audit event has to carry the count.
        var affected = await AffectedBookingsAsync(resourceId, startsAt, endsAt, cancellationToken);

        if (block is null)
        {
            block = new ResourceBlock
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                ResourceDocumentId = resourceId,
                BlockDocumentId = document.Id,
                BlockedByUserId = version.CreatedByUserId,
                BlockedByServiceAccountId = version.CreatedByServiceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
                StartsAtUtc = startsAt.ToUniversalTime(),
                EndsAtUtc = endsAt.ToUniversalTime(),
            };
            _dbContext.ResourceBlocks.Add(block);
        }

        // A move between two resources' Maintenance collections re-points the block, exactly as a booking's
        // holding claim follows its document. There is only ever one row, so no ambiguity arises.
        block.ResourceDocumentId = resourceId;
        block.StartsAtUtc = startsAt.ToUniversalTime();
        block.EndsAtUtc = endsAt.ToUniversalTime();
        block.Status = willBeActive ? BlockStatus.Active : BlockStatus.Cleared;

        var resourceName = await _dbContext.Documents
            .Where(d => d.Id == resourceId)
            .Select(d => d.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? resourceId.ToString();

        // Three facts, three actions — the pair the owner asked for, plus the ordinary edit. Which one this
        // is depends on the TRANSITION, not on the new state alone: a block written again while already
        // active is somebody adjusting a window, not a second grounding.
        var action = (wasActive, willBeActive) switch
        {
            (false, true) => Controllers.AuditActions.BookingSuspended,
            (true, false) => Controllers.AuditActions.BookingRevived,
            _ => Controllers.AuditActions.BookingChanged,
        };

        var effect = action switch
        {
            Controllers.AuditActions.BookingSuspended when affected.Count > 0 => $", suspending {affected.Count} booking(s)",
            Controllers.AuditActions.BookingRevived when affected.Count > 0 => $", reviving {affected.Count} booking(s)",
            Controllers.AuditActions.BookingChanged => string.Empty,
            _ => " (no bookings affected)",
        };

        // The audit event is BUILT here and RECORDED by the caller, after the save — the same rule the
        // booking path learned the hard way (ADR 0777): IAuditRecorder.RecordAsync saves, so recording
        // inline would flush the staged row outside the try/catch that translates an invariant refusal.
        // The people to tell, carried out to the caller with the audit event and delivered after the save for
        // the same reason: INotificationService saves, so notifying inline would flush the staged block row
        // outside the try/catch that translates an invariant refusal (ADR 0777's rule, second application).
        var notice = action switch
        {
            Controllers.AuditActions.BookingSuspended =>
                new PendingBlockNotice(NotificationType.BookingSuspended, document.TenantId, resourceName, startsAt, endsAt, affected),
            Controllers.AuditActions.BookingRevived =>
                new PendingBlockNotice(NotificationType.BookingRevived, document.TenantId, resourceName, startsAt, endsAt, affected),
            _ => null,
        };

        return new PendingBookingAudit(action, $"{startsAt:u}-{endsAt:u} on {resourceName}{effect}", notice);
    }

    /// <summary>Which Active bookings of the resource overlap this window (ADR 0778).</summary>
    /// <remarks>
    /// The number the audit event carries. Suspension is derived and therefore true only while the block is
    /// Active, so this count is the ONLY durable record that those flights were ever stopped — which is why
    /// it is measured at the moment of the act rather than reconstructed later.
    /// </remarks>
    private async Task<List<Guid>> AffectedBookingsAsync(
        Guid resourceId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
    {
        var from = startsAt.ToUniversalTime();
        var to = endsAt.ToUniversalTime();
        var candidates = await _dbContext.ResourceBookings
            .Where(b => b.ResourceDocumentId == resourceId && b.Status == BookingStatus.Active)
            .Select(b => new { b.BookingDocumentId, b.StartsAtUtc, b.EndsAtUtc })
            .ToListAsync(cancellationToken);

        // In memory for the reason every range test in this area is: the SQLite provider cannot translate
        // DateTimeOffset range predicates, and one resource's active bookings are few by nature.
        return [.. candidates
            .Where(b => b.StartsAtUtc < to && from < b.EndsAtUtc)
            .Select(b => b.BookingDocumentId)
            .Distinct()];
    }

    /// <summary>
    /// Whether whoever wrote this version may ground the resource, or return it to service (ADR 0778).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved from the VERSION'S CREATOR rather than from an ambient principal accessor, because this door
    /// is reached from paths that have no ambient principal — a worker, a protocol edge, a drop-upload
    /// finalized later. The version records who wrote the bytes on every one of them, which is precisely the
    /// question being asked, and it is the same source the booking path uses to decide who holds a slot.
    /// </para>
    /// <para>
    /// A SERVICE ACCOUNT can ground but never release (owner decision): grounding on suspicion should be
    /// broad — a maintenance integration or an hours counter reaching a limit ought to be able to stop an
    /// aircraft being booked — while "airworthy again" is a certifying act, and nothing here could tell a
    /// considered release from a bug in an integration.
    /// </para>
    /// </remarks>
    private async Task EnsureBlockRightsAsync(
        DocumentVersion version, bool wasActive, bool willBeActive, CancellationToken cancellationToken)
    {
        // Nothing is changing about the resource's availability — an edit to a cleared block's text, say.
        if (wasActive == willBeActive && !willBeActive)
        {
            return;
        }

        var (canBlock, canRelease) = await BlockRightsOfAsync(version, cancellationToken);

        if (willBeActive && !canBlock)
        {
            throw new ResourceBlockRightRequiredException(
                "Taking a resource out of service requires the 'Block resources' right (ADR 0778).");
        }

        if (wasActive && !willBeActive && !canRelease)
        {
            throw new ResourceReleaseRightRequiredException(
                "Returning a resource to service requires the 'Release resources' right (ADR 0778); "
                + "a service account never holds it.");
        }
    }

    private async Task<(bool CanBlock, bool CanRelease)> BlockRightsOfAsync(
        DocumentVersion version, CancellationToken cancellationToken)
    {
        if (version.CreatedByUserId is { } userId)
        {
            // A tenant admin holds both implicitly, as they do every other system right — consistent rather
            // than a new exception carved out for this one pair.
            var rights = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            return (rights.IsTenantAdmin || rights.CanBlockResources, rights.IsTenantAdmin || rights.CanReleaseResources);
        }

        if (version.CreatedByServiceAccountId is { } serviceAccountId)
        {
            var canBlock = await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanBlockResources)
                .FirstOrDefaultAsync(cancellationToken);

            // Release is deliberately unreachable for a machine — there is no column to grant.
            return (canBlock, false);
        }

        // A version with neither creator cannot happen through an admitted write (a CHECK constraint
        // enforces exactly one), so this is the defensive branch: no principal, no authority.
        return (false, false);
    }


    /// <summary>
    /// Tells the people on the affected bookings that their resource went out of service, or came back
    /// (ADR 0778, slice 4b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Filtered against the DERIVED state, after the save.</b> The candidates are the bookings whose window
    /// this block overlaps, but overlap alone does not decide the message: a booking still caught by ANOTHER
    /// active block is not revived when this one clears, and telling its pilot their flight is back on would
    /// be worse than saying nothing. Suspension is derived, so the honest answer only exists once the row has
    /// been written — which is also why this runs here rather than beside the row.
    /// </para>
    /// <para>
    /// <b>Who counts as affected</b> is the claimants plus the booker. A claimant is found through
    /// <c>ResourcePrincipal</c> (ADR 0775) — the table that says a resource document REPRESENTS a person —
    /// because <c>BookedByUserId</c> is the booker, so a student booked by their instructor would not match,
    /// and that is exactly the case that matters. The booker is included as well: they made a commitment on
    /// somebody's behalf and are the one who will have to unmake it.
    /// </para>
    /// <para>
    /// Best-effort, and deliberately last: a failure to notify must not undo a grounding. The block is already
    /// committed and audited by the time this runs.
    /// </para>
    /// </remarks>
    private async Task NotifyAffectedAsync(PendingBlockNotice notice, CancellationToken cancellationToken)
    {
        if (notice.CandidateBookingIds.Count == 0)
        {
            return;
        }

        var suspended = await BookingSuspension.SuspendedAmongAsync(_dbContext, notice.CandidateBookingIds, cancellationToken);
        var subjects = notice.Type == NotificationType.BookingSuspended
            ? notice.CandidateBookingIds.Where(suspended.Contains).ToList()
            : notice.CandidateBookingIds.Where(id => !suspended.Contains(id)).ToList();
        if (subjects.Count == 0)
        {
            return;
        }

        var claims = await _dbContext.ResourceBookings
            .Where(b => subjects.Contains(b.BookingDocumentId) && b.Status == BookingStatus.Active)
            .Select(b => new { b.BookingDocumentId, b.ResourceDocumentId, b.BookedByUserId })
            .ToListAsync(cancellationToken);

        var resourceIds = claims.Select(c => c.ResourceDocumentId).Distinct().ToList();
        var principals = await _dbContext.ResourcePrincipals
            .Where(p => resourceIds.Contains(p.ResourceDocumentId))
            .Select(p => new { p.ResourceDocumentId, p.UserId })
            .ToListAsync(cancellationToken);
        var personOf = principals.ToDictionary(p => p.ResourceDocumentId, p => p.UserId);

        // One notification per person per booking: two suspended flights are two things to deal with, and
        // these types are non-coalescable for that reason.
        var targets = new HashSet<(Guid UserId, Guid BookingDocumentId)>();
        foreach (var claim in claims)
        {
            if (personOf.TryGetValue(claim.ResourceDocumentId, out var claimantId))
            {
                targets.Add((claimantId, claim.BookingDocumentId));
            }

            if (claim.BookedByUserId is { } bookerId)
            {
                targets.Add((bookerId, claim.BookingDocumentId));
            }
        }

        var window = $"{notice.From:yyyy-MM-dd HH:mm}–{notice.To:HH:mm} UTC";
        var (title, body) = notice.Type == NotificationType.BookingSuspended
            ? ($"{notice.ResourceName} is out of service",
               $"Your booking is on hold: {notice.ResourceName} is unavailable {window}.")
            : ($"{notice.ResourceName} is back in service",
               $"Your booking stands again: {notice.ResourceName} was unavailable {window}.");

        foreach (var (userId, bookingDocumentId) in targets)
        {
            // The tenant is NAMED rather than taken from the ambient accessor: this door is reached from a
            // worker and from protocol edges where none is set, and the ambient overload would drop the
            // notification (loudly now, but still dropped).
            await _notifications.NotifyInTenantAsync(
                notice.TenantId, userId, notice.Type, title, body, bookingDocumentId, cancellationToken);
        }
    }

    /// <summary>What to record once the booking has actually been saved (#1092, ADR 0777).</summary>
    private sealed record PendingBookingAudit(string Action, string Details, PendingBlockNotice? Notice = null);

    /// <summary>Who to tell that a resource went out of service, or came back (ADR 0778, slice 4b).</summary>
    private sealed record PendingBlockNotice(
        NotificationType Type, Guid TenantId, string ResourceName,
        DateTimeOffset From, DateTimeOffset To, IReadOnlyList<Guid> CandidateBookingIds);

    /// <summary>The resources claimed by this booking other than the holding one.</summary>
    private static HashSet<Guid> AttendeeResourceIds(List<ResourceBooking> claims, Guid holdingResourceId) =>
        [.. claims.Select(c => c.ResourceDocumentId).Where(id => id != holdingResourceId)];

    private static ResourceBooking NewClaim(
        Document document, DocumentVersion version, Guid resourceId, DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = document.TenantId,
            ResourceDocumentId = resourceId,
            BookingDocumentId = document.Id,
            StartsAtUtc = startsAt.ToUniversalTime(),
            EndsAtUtc = endsAt.ToUniversalTime(),
            Status = BookingStatus.Active,
            BookedByUserId = version.CreatedByUserId,
            BookedByServiceAccountId = version.CreatedByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// The event's <c>ATTENDEE</c>s become claims of their own (ADR 0776) — a training flight occupies the
    /// aircraft, the student and the instructor, from one PUT by any calendar client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in a controller, because ADR 0744's rule is that EVERY path writing the `.ics` is a
    /// booking act: the bookings endpoint, a drop-upload and a CalDAV PUT all arrive at this one method, and
    /// an expansion living in one of them would be an expansion the other two do not do.
    /// </para>
    /// <para>
    /// An attendee the archive cannot turn into a claim REFUSES the write. Dropping it would book the
    /// resource alone while the author believes a second participant is coming — a booking that looks
    /// complete to everyone and is not.
    /// </para>
    /// <para>
    /// Deliberately NOT RFC 6638 scheduling: no iTIP inbox, no iMIP mail, no PARTSTAT round trip. The
    /// attendee list is read as a statement of who is on the flight, and the conflict invariant answers it.
    /// </para>
    /// </remarks>
    private async Task ReconcileAttendeeClaimsAsync(
        Document document, DocumentVersion version, Ical.Net.CalendarComponents.CalendarEvent occurrence,
        List<ResourceBooking> claims, ResourceBooking holding, DateTimeOffset startsAt, DateTimeOffset endsAt,
        CancellationToken cancellationToken)
    {
        var addresses = occurrence.Attendees
            .Select(a => a.Value?.ToString()?.Replace("mailto:", string.Empty, StringComparison.OrdinalIgnoreCase))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wanted = new HashSet<Guid>();
        foreach (var address in addresses)
        {
            wanted.Add(await ResolveAttendeeResourceAsync(address, cancellationToken));
        }

        // An attendee resolving to the resource that already holds the booking is not a second claim — the
        // organiser inviting the room itself is ordinary, and a duplicate claim would be refused as an overlap.
        wanted.Remove(holding.ResourceDocumentId);

        foreach (var resourceId in wanted.Where(id => claims.All(c => c.ResourceDocumentId != id)))
        {
            _dbContext.ResourceBookings.Add(NewClaim(document, version, resourceId, startsAt, endsAt));
        }

        // Removed from the attendee list means removed from the flight: the claim goes, freeing that person's
        // slot. Cancelled rather than deleted, so the history of who was on it survives (ADR 0735).
        foreach (var dropped in claims.Where(c => c != holding && !wanted.Contains(c.ResourceDocumentId)))
        {
            dropped.Status = BookingStatus.Cancelled;
        }
    }

    /// <summary>The bookable resource standing for this address, or a refusal naming it.</summary>
    /// <remarks>
    /// Two lookups, and both must succeed: the address has to belong to a user of this tenant, and a
    /// <c>ResourcePrincipal</c> has to say which document represents them. A user with no resource is not an
    /// error in the archive — it is somebody nobody has made bookable — but it IS a refusal here, because the
    /// alternative is a flight silently missing a participant.
    ///
    /// Matched on <c>NormalizedEmail</c>, which is what makes the comparison case-insensitive (ADR 0150): an
    /// invitation addressed to Anna@school and a user stored as anna@school are the same person, and a client
    /// composing the address from a display name will not match the stored casing.
    /// </remarks>
    private async Task<Guid> ResolveAttendeeResourceAsync(string address, CancellationToken cancellationToken)
    {
        var normalized = address.ToUpperInvariant();
        var userId = await _dbContext.Users
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId is not { } id)
        {
            throw new BookingAttendeeUnknownException(address);
        }

        var resourceIds = await _dbContext.ResourcePrincipals
            .Where(p => p.UserId == id)
            .Select(p => p.ResourceDocumentId)
            .ToListAsync(cancellationToken);

        return resourceIds.Count switch
        {
            1 => resourceIds[0],
            0 => throw new BookingAttendeeUnknownException(address),

            // Two resources for one person is legal (the mapping is unique on the RESOURCE, not the user) and
            // leaves this with no way to choose. Refused rather than guessed: booking the wrong one of
            // somebody's two calendars is a mistake nobody would look for.
            _ => throw new BookingAttendeeAmbiguousException(address, resourceIds.Count),
        };
    }

    /// <summary>The instant a calendar time names — <see cref="Stamp"/>'s twin for the claim row.</summary>
    /// <remarks>An all-day value becomes midnight in the SERVER's zone, the same deliberate floating-time
    /// rule Stamp documents: one comparable instant beats an invented UTC midnight nobody's wall clock
    /// shows.</remarks>
    private static DateTimeOffset? Instant(Ical.Net.DataTypes.CalDateTime? when)
    {
        if (when?.Value is not { } value)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var offset = !when.HasTime || string.IsNullOrEmpty(when.TzId)
            ? TimeZoneInfo.Local.GetUtcOffset(local)
            : ZoneOffset(when.TzId, local);
        return new DateTimeOffset(local, offset);
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The stored <c>RRULE</c> as text, or null when the entry does not repeat.</summary>
    /// <remarks>
    /// Round-tripped through the library's own writer rather than scraped out of the blob, so what is indexed
    /// is what the file actually says — the same source the composer reads it from, which is what keeps the
    /// index and the item from disagreeing about whether something repeats.
    /// </remarks>
    private static string? RecurrenceRule(Ical.Net.CalendarComponents.CalendarEvent occurrence) =>
        Nonempty(occurrence.RecurrenceRule?.ToString());

    // Assigns the mask version, REPLACE-writes the field values that parsed, and names the document after
    // its human title (summary / display name) when the upload carried a placeholder-ish name — same
    // spirit as an email being named after its subject. Replace rather than append (ADR 0744): the same
    // method now also REFRESHES an edited item, and appending would keep the value the edit removed.
    private async Task ApplyAsync(
        Document document, Guid maskVersionId, IReadOnlyList<(string Field, string? Value)> values, string? title, CancellationToken cancellationToken)
    {
        var fieldIdsByName = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId)
            .Select(f => new { f.Name, f.Id })
            .ToDictionaryAsync(f => f.Name, f => f.Id, cancellationToken);

        // Every field this pass OWNS is cleared before the surviving values are written back — including
        // one whose new value is null: an edited-away Location must not linger as the old one.
        var ownedFieldIds = values
            .Where(v => fieldIdsByName.ContainsKey(v.Field))
            .Select(v => fieldIdsByName[v.Field])
            .ToList();
        var stale = await _dbContext.FieldValues
            .Where(v => v.DocumentId == document.Id && ownedFieldIds.Contains(v.FieldDefinitionId))
            .ToListAsync(cancellationToken);
        _dbContext.FieldValues.RemoveRange(stale);

        document.MaskVersionId = maskVersionId;

        // Renamed to the item's own title (FN / SUMMARY) — but only when no sibling already holds that name.
        // Without the check the save throws the sibling-name invariant, which surfaces as a bare 500: two
        // contacts called "Ada Lovelace", or two appointments called "Standup", are entirely ordinary, and a
        // person may well know both. The email path next door has had this guard all along; this one did not,
        // so the collision was reachable through a CalDAV/CardDAV PUT as well as through the create endpoint
        // that found it (#631).
        if (Nonempty(title) is { } name)
        {
            var collides = await _dbContext.Documents.AnyAsync(
                d => d.Id != document.Id && d.ParentId == document.ParentId && d.Name == name, cancellationToken);
            if (!collides)
            {
                document.Name = name;
            }
        }

        foreach (var (field, value) in values)
        {
            if (value is null || !fieldIdsByName.TryGetValue(field, out var fieldDefinitionId))
            {
                continue;
            }

            _dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = fieldDefinitionId,
                Value = value,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// An index value for a calendar instant: ISO-8601 <b>carrying an offset</b> (#660, ADR 0647).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zone is the entry's OWN where it has one (<c>TZID</c>, or <c>Z</c> for a UTC stamp), and the
    /// SERVER's where the entry floats. So every indexed moment is a real instant that sorts against every
    /// other — a 19:00 concert in Barcelona and a 19:00 concert in Massachusetts are four hours apart and now
    /// order that way, where a bare wall clock made them a tie.
    /// </para>
    /// <para>
    /// This is a PROJECTION, not the source of truth. The stored <c>.ics</c> keeps its floating time exactly as
    /// ADR 0631 requires, so DAV clients and the structured editors round-trip unchanged; only the searchable
    /// copy gains the offset. The cost is that a floating entry's index value depends on where the server runs
    /// — reindex on a host in another zone and it shifts — which is the deliberate price of one comparable
    /// instant.
    /// </para>
    /// <para>
    /// An ALL-DAY entry keeps a plain date. A day is not a moment: it has no time to place in a zone, and
    /// stamping midnight on it would invent one — the same inference this rule exists to avoid elsewhere.
    /// </para>
    /// </remarks>
    private static string? Stamp(Ical.Net.DataTypes.CalDateTime? when)
    {
        if (when?.Value is not { } value)
        {
            return null;
        }

        if (!when.HasTime)
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var offset = when.TzId switch
        {
            null or "" => TimeZoneInfo.Local.GetUtcOffset(local),
            var tz => ZoneOffset(tz, local),
        };

        return new DateTimeOffset(local, offset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>The zone's offset at that moment, falling back to the server's when the id is unknown here.</summary>
    /// <remarks>
    /// A TZID names an IANA zone the host may not carry (a Windows host without ICU, say). Falling back keeps
    /// the item indexed and findable rather than dropping its time entirely, which is the failure that would
    /// make a whole calendar unsortable because of one exotic zone.
    /// </remarks>
    private static TimeSpan ZoneOffset(string timeZoneId, DateTime local)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(local);
        }
        catch (Exception)
        {
            return TimeZoneInfo.Local.GetUtcOffset(local);
        }
    }
}
