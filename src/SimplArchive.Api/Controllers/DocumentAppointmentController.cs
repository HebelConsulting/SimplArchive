using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
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
    private readonly DocumentFinalizer _finalizer;
    private readonly ICurrentUserAccessor _currentUser;

    public DocumentAppointmentController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        IAppointmentComposer composer,
        DocumentFinalizer finalizer,
        ICurrentUserAccessor currentUser)
    {
        _dbContext = dbContext;
        _access = access;
        _storage = storage;
        _composer = composer;
        _finalizer = finalizer;
        _currentUser = currentUser;
    }

    // Plain mutable classes, not records — XmlSerializer (ADRs 0189/0190).
    public class AppointmentResource : HypermediaResource
    {
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
        public string? TimeZoneId { get; set; }

        public string? Location { get; set; }

        public string? Description { get; set; }

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
        TimeZoneId = appointment.TimeZoneId,
        Location = appointment.Location,
        Description = appointment.Description,
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
        r.TimeZoneId,
        r.Location,
        r.Description,
        r.RecurrenceRule);
}
