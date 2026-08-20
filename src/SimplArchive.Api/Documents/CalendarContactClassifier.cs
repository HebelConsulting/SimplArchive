using System.Globalization;
using FolkerKinzel.VCards;
using SimplArchive.Application.Abstractions;
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
    /// whether it did. The caller has already established the document is still unclassified.
    /// </summary>
    public async Task<bool> TryClassifyAsync(Document document, DocumentVersion version, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(version.ObjectKey).ToLowerInvariant();
        if (!Handles(extension))
        {
            return false;
        }

        string content;
        try
        {
            await using var stream = await _objectStorageClient.GetObjectAsync(version.ObjectKey, cancellationToken);
            using var reader = new StreamReader(stream);
            content = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not read {ObjectKey} for calendar/contact classification", version.ObjectKey);
            return false;
        }

        return extension switch
        {
            ".vcf" => await ClassifyContactAsync(document, version, content, cancellationToken),
            ".ics" => await ClassifyCalendarAsync(document, version, content, cancellationToken),
            _ => false,
        };
    }

    private async Task<bool> ClassifyContactAsync(Document document, DocumentVersion version, string content, CancellationToken cancellationToken)
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

        await ApplyAsync(document, WellKnownMaskIds.Contact, values, fullName, cancellationToken);
        return true;
    }

    private async Task<bool> ClassifyCalendarAsync(Document document, DocumentVersion version, string content, CancellationToken cancellationToken)
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
            // Indexed so a listing can SAY the entry repeats without opening the blob. The rule itself stays
            // opaque — this is the stored text, not an interpretation of it, and nothing here expands a
            // recurrence set. What it buys is honesty in the grid: an entry drawn at its first occurrence and
            // nowhere else is under-reporting the month, and a marker is what stops that being silent.
            ("Repeats", Nonempty(RecurrenceRule(occurrence))),
        };

        await ApplyAsync(document, WellKnownMaskIds.Appointment, values, Nonempty(occurrence.Summary), cancellationToken);

        if (start is { } startDate)
        {
            version.DocumentDate = DateOnly.FromDateTime(startDate);
        }

        return true;
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

    // Assigns the mask, writes the field values that parsed, and names the document after its human title
    // (summary / display name) when the upload carried a placeholder-ish name — same spirit as an email being
    // named after its subject.
    private async Task ApplyAsync(
        Document document, Guid maskId, IReadOnlyList<(string Field, string? Value)> values, string? title, CancellationToken cancellationToken)
    {
        var maskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, document.TenantId, maskId, cancellationToken);
        if (maskVersionId is null)
        {
            // The mask is not seeded for this tenant — leave the document unclassified rather than half-typed.
            _logger.LogWarning("Mask {MaskId} is not seeded for tenant {TenantId}; leaving {DocumentId} unclassified",
                maskId, document.TenantId, document.Id);
            return;
        }

        var fieldIdsByName = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId)
            .Select(f => new { f.Name, f.Id })
            .ToDictionaryAsync(f => f.Name, f => f.Id, cancellationToken);

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
