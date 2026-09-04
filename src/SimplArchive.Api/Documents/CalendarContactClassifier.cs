using System.Globalization;
using FolkerKinzel.VCards;
using SimplArchive.Api.Errors.Exceptions.Booking;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
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

    public CalendarContactClassifier(
        SimplArchiveDbContext dbContext, IObjectStorageClient objectStorageClient,
        IContactCardComposer contacts, ILogger<CalendarContactClassifier> logger)
    {
        _dbContext = dbContext;
        _objectStorageClient = objectStorageClient;
        _contacts = contacts;
        _logger = logger;
    }

    /// <summary>The extensions this classifier owns.</summary>
    public static bool Handles(string extension) => extension is ".vcf" or ".ics";

    /// <summary>
    /// Classifies the document behind <paramref name="version"/> when it is a vCard/iCalendar, returning
    /// whether it did. The caller has already established the document is still unclassified and that its
    /// destination admits <paramref name="itemMaskId"/> — which is also what SAYS the mask: an .ics is an
    /// Appointment in a Calendar and a Room booking in a Schedule (ADR 0744, the Note/eMail precedent —
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
    /// every listing. For a Room booking the same pass moves the claim row's slot, which is what makes an
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

        var values = new List<(string Field, string? Value)>
        {
            ("Event UID", Nonempty(occurrence.Uid) ?? document.Id.ToString()),
            ("Start", Stamp(occurrence.DtStart)),
            ("End", Stamp(occurrence.DtEnd)),
            ("Location", Nonempty(occurrence.Location)),
        };

        if (maskId == WellKnownMaskIds.RoomBooking)
        {
            // The booking IS the .ics (ADR 0744): the same pass that indexes the fields moves the claim.
            // Deliberately BEFORE ApplyAsync, so the row rides the same save as the fields and the
            // DbContext's overlap invariant judges them together — refusing a conflicting write on every
            // path (a CalDAV PUT, a drop-upload, the booking endpoint) at the one door they all use.
            values.Add(("Purpose", Nonempty(occurrence.Description)));
            await UpsertBookingRowAsync(document, version, occurrence, cancellationToken);
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
            // Translated by FACT (the Kind), never by matching message text — so a slot conflict is a 409
            // with its own code on every upload path, instead of whatever a blanket catch assumes.
            throw e.Kind switch
            {
                BookingInvariantKind.SlotTaken => new BookingSlotConflictException(e.Message),
                BookingInvariantKind.SlotWithoutExtent => new BookingSlotInvalidException(e.Message),
                _ => new ResourceNotBookableException(e.Message),
            };
        }

        if (start is { } startDate)
        {
            version.DocumentDate = DateOnly.FromDateTime(startDate);
        }

        return true;
    }

    /// <summary>Creates or moves the <see cref="ResourceBooking"/> claim behind a Schedule's .ics (ADR 0744).</summary>
    /// <remarks>
    /// The row is authoritative for the slot; the indexed Start/End are its projection. A new row's booker
    /// is the version's creator — on a CalDAV PUT that is the authenticated DAV user, on the booking
    /// endpoint the caller, so "who holds the slot" is right on every path.
    /// </remarks>
    private async Task UpsertBookingRowAsync(
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

        // The room is the Schedule's parent — containment guarantees the shape (a Room booking lives only
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

        var row = await _dbContext.ResourceBookings
            .FirstOrDefaultAsync(b => b.BookingDocumentId == document.Id, cancellationToken);
        if (row is null)
        {
            row = new ResourceBooking
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                BookingDocumentId = document.Id,
                Status = BookingStatus.Active,
                BookedByUserId = version.CreatedByUserId,
                BookedByServiceAccountId = version.CreatedByServiceAccountId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.ResourceBookings.Add(row);
        }

        row.ResourceDocumentId = resourceId;
        row.StartsAtUtc = startsAt.ToUniversalTime();
        row.EndsAtUtc = endsAt.ToUniversalTime();
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
