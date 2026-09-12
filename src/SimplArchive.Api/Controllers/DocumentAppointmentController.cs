using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Presentation;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The structured, editable view of an appointment's iCalendar entry (#564, ADR 0631).
/// </summary>
/// <remarks>
/// <para>
/// The calendar sibling of <see cref="DocumentContactCardController"/>: <c>GET</c> returns the six modelled
/// fields, <c>PUT</c> merges them back into the stored component. The merge is
/// <see cref="IAppointmentComposer"/>'s, which leaves everything the form does not model exactly as it was —
/// <c>VALARM</c> above all, since a reminder someone set on their phone is what a rewrite loses first.
/// </para>
/// <para>
/// Attendees are returned so the form can show who is invited and how they replied, and are deliberately not
/// editable (ADR 0631 decision 3): this product never sends a scheduling message, so an attendee edit would
/// only make the user's copy diverge from everyone else's.
/// </para>
/// <para>
/// A save writes a <b>new version</b>, as a DAV <c>PUT</c> or an upload does, so history, indexing, WORM and
/// retention behave as they do for any other edit.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/appointment")]
[Authorize]
public class DocumentAppointmentController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly IObjectStorageClient _storage;
    private readonly IAppointmentComposer _composer;
    private readonly TypedItemWriter _writer;
    private readonly DocumentFinalizer _finalizer;
    private readonly ICurrentUserAccessor _currentUser;

    public DocumentAppointmentController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        IAppointmentComposer composer,
        DocumentFinalizer finalizer,
        TypedItemWriter writer,
        ICurrentUserAccessor currentUser)
    {
        _dbContext = dbContext;
        _access = access;
        _storage = storage;
        _composer = composer;
        _finalizer = finalizer;
        _writer = writer;
        _currentUser = currentUser;
    }

    // Plain mutable classes, not records — XmlSerializer (ADRs 0189/0190).
    public class AppointmentResource : HypermediaResource
    {
        /// <summary>
        /// Which occurrences of a repeating entry a PUT changes — <c>all</c>, <c>this</c> or <c>following</c>
        /// (#1133). Request-only, and ignored for an entry that does not repeat.
        /// </summary>
        /// <remarks>
        /// Editing a series without this choice applies every change to the WHOLE series, including
        /// occurrences already past — which is why the editors showed recurrence read-only until the choice
        /// existed. <c>all</c> is the default, because that is what a PUT of a series has always meant.
        /// </remarks>
        public string? Scope { get; set; }

        /// <summary>
        /// WHICH occurrence <see cref="Scope"/> is about — the instant the listing gave as its
        /// <c>recurrenceId</c>. Required for <c>this</c> and <c>following</c>, meaningless for <c>all</c>.
        /// </summary>
        public string? RecurrenceId { get; set; }

        public string? Summary { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTime? Start { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTime? End { get; set; }

        public bool IsAllDay { get; set; }

        /// <summary>
        /// The appointment's OWN zone — a TZID, "UTC", or null for a floating time. The form labels the times
        /// with it and sends it back unchanged; nothing on this path converts between zones (ADR 0631
        /// decision 5), which is what keeps a weekly meeting from drifting across a daylight-saving change.
        /// </summary>
        /// <remarks>
        /// Superseded by <see cref="StartTimeZoneId"/>/<see cref="EndTimeZoneId"/> (ADR 0690) and kept as the
        /// START's zone for a client that predates them: on a PUT it is used only when neither of the two is
        /// sent, so an older desktop build still round-trips an ordinary single-zone appointment rather than
        /// stripping the zone off it. It cannot express an entry whose endpoints differ — which is exactly what
        /// it did before, silently.
        /// </remarks>
        public string? TimeZoneId { get; set; }

        /// <summary>The zone <see cref="Start"/> is written in (ADR 0690).</summary>
        public string? StartTimeZoneId { get; set; }

        /// <summary>
        /// The zone <see cref="End"/> is written in, which iCalendar allows to DIFFER from the start's — a
        /// flight leaving Zurich at 09:00 and landing in Boston at 11:30 is one appointment with two zones.
        /// </summary>
        public string? EndTimeZoneId { get; set; }

        public string? Location { get; set; }

        public string? Description { get; set; }

        /// <summary>The <c>URL</c> property — where the event lives online. Absolute, or the save is refused.</summary>
        public string? Url { get; set; }

        /// <summary>The RRULE as raw text. Opaque — the server never expands a recurrence set.</summary>
        public string? RecurrenceRule { get; set; }

        /// <summary>Read-only: who is invited and how they replied. Ignored on PUT.</summary>
        public List<AttendeeResource> Attendees { get; set; } = [];

        /// <summary>Read-only: how many reminders the entry carries, so the form can say one is set without
        /// implying it can be changed here.</summary>
        public int ReminderCount { get; set; }

        /// <summary>False when the caller may read but not save — so the client disables Edit rather than
        /// offering an action the server will refuse.</summary>
        public bool CanEdit { get; set; }
    }

    public class AttendeeResource
    {
        public string? Name { get; set; }

        public string? Address { get; set; }

        public string? Status { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken) =>
        await ReadAsync(documentId, body: true, cancellationToken);

    /// <summary>Same headers, no body — ASP.NET Core does not strip a GET body for HEAD.</summary>
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken) =>
        await ReadAsync(documentId, body: false, cancellationToken);

    private async Task<IActionResult> ReadAsync(Guid documentId, bool body, CancellationToken cancellationToken)
    {
        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return Forbid();
        }

        if (await ResolveAsync(documentId, cancellationToken) is not var (document, blob))
        {
            return NotFound();
        }

        var resource = ToResource(_composer.Read(blob), blob);
        resource.CanEdit = rights.CanEditContent;
        resource.Links.Add(new Link("self", $"/api/documents/{documentId}/appointment", "GET"));
        resource.Links.Add(new Link("document", $"/api/documents/{documentId}", "GET"));

        // The RAW source behind this resource (#648, ADR 0643) — how a user sees, and changes, the properties
        // this form does not model. Advertised HERE rather than on the document, so a client that has just read
        // the structured item already holds the address and does not spend a request to learn it (ADR 0557).
        // Withheld from a caller who cannot read content, which the Forbid above already settled.
        resource.Links.Add(new Link("source", DocumentItemSourceController.SourceHref(documentId, isContact: false), "GET"));

        // The DOCUMENT's token: a version is append-only and carries none, and a concurrent save moves the
        // document's, which is exactly the collision this needs to detect.
        Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
        return body ? Ok(resource) : new EmptyResult();
    }

    [HttpPut]
    public async Task<IActionResult> Put(
        Guid documentId,
        [FromBody] AppointmentResource request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            throw new Errors.Exceptions.Concurrency.IfMatchRequiredException();
        }

        if (await ResolveAsync(documentId, cancellationToken) is not var (document, blob))
        {
            return NotFound();
        }

        if (ifMatch.Trim('"', 'W', '/') != document.ConcurrencyToken.ToString())
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
        }

        // The UID is the correlation key a later DAV PUT matches on, so it comes from the STORED entry and
        // never from the request: a client that sent a different one would fork the appointment into a
        // duplicate on the next sync.
        var uid = await _dbContext.FieldValueAsync(documentId, "Event UID", cancellationToken)
                  ?? documentId.ToString();

        // "This occurrence" and "this and following" both AMEND the series and file a new entry beside it
        // (#1133). Handled before the ordinary merge, because they write a different blob to a different
        // document — and in one request, so a half-applied edit cannot leave an occurrence cancelled with
        // nothing put in its place.
        if (ScopedEdit.Parse(request.Scope) is { } scope && scope != ScopedEdit.Scope.All)
        {
            if (await SplitSeriesAsync(document, blob, request, scope, uid, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
            return NoContent();
        }

        var merged = _composer.Merge(blob, FromResource(request), uid);

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var storageFolderId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, now, storageFolderId, versionId, ".ics");
        await _storage.PutObjectAsync(
            objectKey, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(merged)), "text/calendar");

        var newVersion = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = document.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        _dbContext.DocumentVersions.Add(newVersion);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Pending + the shared finalizer, never a hand-written Confirmed version: the status is guarded by a
        // CHECK constraint, and the finalizer is what re-extracts the index fields from the merged entry.
        await _finalizer.FinalizeAsync(newVersion, cancellationToken);

        // The content changed, so the token both editors share must move — see StructuredItemVersioning.
        await StructuredItemVersioning.MarkContentChangedAsync(_dbContext, document, cancellationToken);

        Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
        return NoContent();
    }

    /// <summary>
    /// Cancels or ends the series at one occurrence and files the edited values as an entry of their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns null when it succeeded, or the refusal to send back. Both halves happen in ONE request, so a
    /// half-applied edit cannot leave an occurrence cancelled with nothing put in its place.
    /// </para>
    /// <para>
    /// The new entry is filed into the SAME collection, so it inherits the containment and the rights the
    /// series already lives under — there is nothing to re-authorise, and the caller's right to edit the
    /// series was settled above.
    /// </para>
    /// </remarks>
    private async Task<IActionResult?> SplitSeriesAsync(
        Document document,
        string blob,
        AppointmentResource request,
        ScopedEdit.Scope scope,
        string uid,
        CancellationToken cancellationToken)
    {
        if (!DateTimeOffset.TryParse(request.RecurrenceId, out var occurrence))
        {
            throw new Errors.Exceptions.Booking.BookingSlotInvalidException(
                "Changing one occurrence needs the recurrenceId the listing gave for it.");
        }

        var stored = _composer.Read(blob);
        if (string.IsNullOrWhiteSpace(stored.RecurrenceRule))
        {
            // Not a series at all: the scope is meaningless, and rewriting the one entry is what the caller
            // plainly meant. Falling through to the ordinary merge would be the same thing with an extra
            // round trip, so say so instead of pretending the choice applied.
            throw new Errors.Exceptions.Booking.BookingSlotInvalidException(
                "This entry does not repeat, so there is no single occurrence to change.");
        }

        if (document.ParentId is not { } collectionId
            || await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == collectionId, cancellationToken) is not { } collection)
        {
            return NotFound();
        }

        // The series, amended. ThisOccurrence cancels the one day; ThisAndFollowing ends the series just
        // before it, so everything already held keeps the values it was held with.
        var amended = scope == ScopedEdit.Scope.ThisOccurrence
            ? _composer.CancelOccurrence(blob, occurrence)
            : _composer.Merge(
                blob,
                stored with
                {
                    RecurrenceRule = RepeatChoices.WithUntil(
                    RepeatChoices.WithoutUntil(stored.RecurrenceRule), DateOnly.FromDateTime(occurrence.UtcDateTime.AddDays(-1)))
                },
                uid);

        await _writer.WriteVersionAsync(document, amended, ".ics", "text/calendar", _currentUser.UserId, cancellationToken);
        await StructuredItemVersioning.MarkContentChangedAsync(_dbContext, document, cancellationToken);

        // ...and the edited values as an entry of their own. A fresh UID, because it is a different entry:
        // reusing the series' would fork it into a duplicate on the next DAV sync.
        var replacement = FromResource(request) with
        {
            // ThisOccurrence is a single entry; ThisAndFollowing carries the series on from here.
            RecurrenceRule = scope == ScopedEdit.Scope.ThisOccurrence ? null : stored.RecurrenceRule,
        };

        var created = _composer.Merge(null, replacement, Guid.NewGuid().ToString());
        await _writer.CreateAsync(
            collection,
            request.Summary is { Length: > 0 } summary ? summary : document.Name,
            created,
            ".ics",
            "text/calendar",
            _currentUser.UserId,
            null,
            cancellationToken);

        return null;
    }

    /// <summary>The document and its current version's bytes — or null if it is not an appointment.</summary>
    private async Task<(Document Document, string Blob)?> ResolveAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var version = await CurrentVersion.ResolveAsync(
            _dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        if (version is null || !version.ObjectKey.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using var stream = await _storage.GetObjectAsync(version.ObjectKey, cancellationToken);
        using var reader = new StreamReader(stream);
        return (document, await reader.ReadToEndAsync(cancellationToken));
    }

    // Attendees and the reminder count are read straight off the blob rather than modelled on Appointment:
    // they are display-only, so putting them on the editable record would invite a future PUT to honour them.
    private static AppointmentResource ToResource(Appointment appointment, string blob) => new()
    {
        Summary = appointment.Summary,
        Start = appointment.Start,
        End = appointment.End,
        IsAllDay = appointment.IsAllDay,
        TimeZoneId = appointment.StartTimeZoneId,
        StartTimeZoneId = appointment.StartTimeZoneId,
        EndTimeZoneId = appointment.EndTimeZoneId,
        Location = appointment.Location,
        Description = appointment.Description,
        Url = appointment.Url,
        RecurrenceRule = appointment.RecurrenceRule,
        Attendees = [.. AppointmentDisplay.Attendees(blob)
            .Select(a => new AttendeeResource { Name = a.Name, Address = a.Address, Status = a.Status })],
        ReminderCount = AppointmentDisplay.ReminderCount(blob),
    };

    /// <summary>Internal so the CREATE shares it — one shape and one conversion for both (#631).</summary>
    internal static Appointment FromResource(AppointmentResource r) => new(
        r.Summary,
        r.Start,
        r.End,
        r.IsAllDay,
        // The per-endpoint zones win; the single TimeZoneId is the fallback for a client that predates them
        // (ADR 0690). Falling back the other way would let a new client's explicit "floating" be overridden by
        // a field it never set.
        r.StartTimeZoneId ?? r.TimeZoneId,
        r.EndTimeZoneId ?? r.StartTimeZoneId ?? r.TimeZoneId,
        r.Location,
        r.Description,
        r.RecurrenceRule,
        r.Url);
}
