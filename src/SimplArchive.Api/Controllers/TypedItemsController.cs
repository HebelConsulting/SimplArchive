using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Creating a contact or an appointment: <c>POST</c> on the collection that holds it (#631).
/// </summary>
/// <remarks>
/// <para>
/// The structured editors (ADR 0631) are <c>PUT</c> on a document that already exists, and
/// <c>POST /documents/{id}/children</c> makes a folder-ish document with no version — so neither could answer
/// "make me a new contact". This is the missing half, and it takes the shape <c>sections</c>/<c>notes</c>
/// already established: a <c>POST</c> on the typed folder, advertised as a rel that gates the client's button.
/// One create, one rel (ADR 0637), so neither client special-cases contacts or calendars.
/// </para>
/// <para>
/// <b>The UID is minted here, never taken from the request.</b> It is the correlation key a later DAV sync
/// matches on, so a client's guess would fork the item into a duplicate on first sync — the same reasoning
/// that made the editors read the UID from the STORED item, after reading it from the wrong place rewrote a
/// vCard's UID to the contact's phone number (#628).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class TypedItemsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly IObjectStorageClient _storage;
    private readonly DocumentFinalizer _finalizer;
    private readonly IContactCardComposer _contacts;
    private readonly IAppointmentComposer _appointments;
    private readonly IAuditRecorder _audit;

    public TypedItemsController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        DocumentFinalizer finalizer,
        IContactCardComposer contacts,
        IAppointmentComposer appointments,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _access = access;
        _storage = storage;
        _finalizer = finalizer;
        _contacts = contacts;
        _appointments = appointments;
        _audit = audit;
    }

    public class CreateContactRequest
    {
        public string? FormattedName { get; set; }
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public string? Organization { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
    }

    public class CreateAppointmentRequest
    {
        public string? Summary { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public bool IsAllDay { get; set; }
        public string? TimeZoneId { get; set; }
        public string? Location { get; set; }
        public string? Description { get; set; }
    }

    [HttpPost("contacts")]
    public async Task<IActionResult> CreateContact(
        Guid documentId, [FromBody] CreateContactRequest request, CancellationToken cancellationToken)
    {
        if (await RequireFolderAsync(documentId, WellKnownMaskIds.Addressbook, cancellationToken) is not { } folder)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        var uid = Guid.NewGuid().ToString();
        var card = new ContactCard(
            request.FormattedName, request.GivenName, request.FamilyName, request.Organization, Title: null,
            Emails: string.IsNullOrWhiteSpace(request.Email) ? [] : [new ContactField(request.Email.Trim(), null)],
            Phones: string.IsNullOrWhiteSpace(request.Phone) ? [] : [new ContactField(request.Phone.Trim(), null)],
            Addresses: [], Birthday: null, Url: null, Note: null);

        // A fresh card: Merge composes one from nothing when the existing blob is null, which is the same path
        // an edit takes minus the preservation — so the two cannot drift in how a vCard is written.
        var blob = _contacts.Merge(null, card, uid);
        var name = FirstNonEmpty(request.FormattedName, Join(request.GivenName, request.FamilyName), request.Organization)
                   ?? "New contact";

        return await CreateAsync(folder, name, blob, ".vcf", "text/vcard", cancellationToken);
    }

    [HttpPost("appointments")]
    public async Task<IActionResult> CreateAppointment(
        Guid documentId, [FromBody] CreateAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (await RequireFolderAsync(documentId, WellKnownMaskIds.Calendar, cancellationToken) is not { } folder)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        var uid = Guid.NewGuid().ToString();
        var appointment = new Appointment(
            request.Summary, request.Start, request.End, request.IsAllDay, request.TimeZoneId,
            request.Location, request.Description, RecurrenceRule: null);

        var blob = _appointments.Merge(null, appointment, uid);
        var name = FirstNonEmpty(request.Summary) ?? "New appointment";

        return await CreateAsync(folder, name, blob, ".ics", "text/calendar", cancellationToken);
    }

    /// <summary>The folder, when it wears the mask this create belongs to; else null.</summary>
    /// <remarks>
    /// NotFound rather than a refusal, exactly as the notebook creates answer: these sub-resources do not
    /// EXIST on an ordinary folder, which is what their absent rel already says. A 403 would imply the caller
    /// might be granted them.
    /// </remarks>
    private async Task<Document?> RequireFolderAsync(Guid documentId, Guid maskId, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (folder?.MaskVersionId is not { } maskVersionId)
        {
            return null;
        }

        var actual = await _dbContext.MaskVersions
            .Where(v => v.Id == maskVersionId)
            .Select(v => (Guid?)v.MaskId)
            .SingleOrDefaultAsync(cancellationToken);

        return actual == maskId ? folder : null;
    }

    /// <summary>
    /// The document + its first version, filed into the collection. Shared, because a contact and an
    /// appointment differ only in the bytes, the extension and the media type.
    /// </summary>
    private async Task<IActionResult> CreateAsync(
        Document folder, string name, string blob, string extension, string contentType, CancellationToken cancellationToken)
    {
        var (createdByUserId, createdByServiceAccountId) = _access.GetCallerIdentity();
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var storageFolderId = Guid.NewGuid();

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = folder.TenantId,
            ParentId = folder.Id,
            Name = await UniqueNameAsync(folder.Id, name, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = now,
            StorageFolderId = storageFolderId,
            // MASKLESS on purpose: the finalizer classifies a .vcf as a Contact and a .ics as an Appointment
            // once the bytes are there, and a typed folder admits only those. Stamping anything here would be
            // guessing at what classification is about to decide (ADR 0641's lesson, from the other side).
        };
        _dbContext.Documents.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var objectKey = ObjectKeyBuilder.Build(folder.TenantId, now, storageFolderId, versionId, extension);
        await _storage.PutObjectAsync(objectKey, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(blob)), contentType, cancellationToken);

        // Pending + the shared finalizer, never a hand-written Confirmed version: the status is guarded by a
        // CHECK constraint, and the finalizer is what classifies the item and extracts its index fields — the
        // UID among them, which is what a later DAV sync matches on.
        var version = new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = folder.TenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = createdByUserId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        };
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _finalizer.FinalizeAsync(version, cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", document.Id, document.Name, cancellationToken: cancellationToken);

        return Created($"/api/documents/{document.Id}", new CreatedResource
        {
            Id = document.Id,
            Links = [new Link("self", $"/api/documents/{document.Id}", "GET")],
        });
    }

    /// <summary>A name no sibling already holds — the DbContext refuses a clash, and a create should not.</summary>
    private async Task<string> UniqueNameAsync(Guid folderId, string name, CancellationToken cancellationToken)
    {
        var taken = await _dbContext.Documents
            .Where(d => d.ParentId == folderId)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);

        if (!taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!taken.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    private static string? Join(string? given, string? family) =>
        string.Join(' ', new[] { given, family }.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } joined
            ? joined
            : null;

    public class CreatedResource : HypermediaResource
    {
        public Guid Id { get; set; }
    }
}
