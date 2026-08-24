using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
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
/// <b>It takes the EDITOR's own resource, not a create-shaped subset.</b> The subset it shipped with was
/// defensible only while New opened a small form; both clients now open the full structured editor empty, so a
/// narrower create is a funnel that silently drops a second phone number, a birthday or a postal address at the
/// exact moment the user first types one. One shape and one conversion for create and edit means there is no
/// second place for the mapping to be wrong, and no field that exists in the form but not on the wire.
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
    private readonly ILogger<TypedItemsController> _logger;

    public TypedItemsController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        DocumentFinalizer finalizer,
        IContactCardComposer contacts,
        IAppointmentComposer appointments,
        IAuditRecorder audit,
        ILogger<TypedItemsController> logger)
    {
        _logger = logger;
        _dbContext = dbContext;
        _access = access;
        _storage = storage;
        _finalizer = finalizer;
        _contacts = contacts;
        _appointments = appointments;
        _audit = audit;
    }

    [HttpPost("contacts")]
    public async Task<IActionResult> CreateContact(
        Guid documentId,
        [FromBody] DocumentContactCardController.ContactCardResource request,
        CancellationToken cancellationToken)
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

        // A fresh card: Merge composes one from nothing when the existing blob is null, which is the same path
        // an edit takes minus the preservation — so the two cannot drift in how a vCard is written.
        var blob = _contacts.Merge(null, DocumentContactCardController.FromResource(request), uid);
        var name = FirstNonEmpty(request.FormattedName, Join(request.GivenName, request.FamilyName), request.Organization)
                   ?? "New contact";

        return await CreateAsync(folder, name, blob, ".vcf", "text/vcard", cancellationToken);
    }

    [HttpPost("appointments")]
    public async Task<IActionResult> CreateAppointment(
        Guid documentId,
        [FromBody] DocumentAppointmentController.AppointmentResource request,
        CancellationToken cancellationToken)
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
        var blob = _appointments.Merge(null, DocumentAppointmentController.FromResource(request), uid);
        var name = FirstNonEmpty(request.Summary) ?? "New appointment";

        return await CreateAsync(folder, name, blob, ".ics", "text/calendar", cancellationToken);
    }

    /// <summary>One listed contact — the card's index fields, so a row renders without a request of its own.</summary>
    public class ContactEntryResource : HypermediaResource
    {
        public Guid Id { get; set; }

        /// <summary>The filed document name, which is what the tree and the tab title show.</summary>
        public string Name { get; set; } = string.Empty;

        public string? FullName { get; set; }

        public string? Email { get; set; }

        public string? Phone { get; set; }

        public string? Organization { get; set; }

        /// <summary>
        /// True when the card carries an inline picture — so a row draws a face rather than initials, without
        /// asking. Its address arrives as the <c>photo</c> rel on this entry, present only when this is true.
        /// </summary>
        public bool HasPhoto { get; set; }
    }

    /// <summary>One listed appointment.</summary>
    public class AppointmentEntryResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The indexed start, verbatim: ISO-8601 with an offset for a timed entry, a plain <c>yyyy-MM-dd</c>
        /// for an all-day one.
        /// </summary>
        /// <remarks>
        /// A string rather than a <c>DateTimeOffset</c>, because the two shapes are genuinely different things
        /// and a single typed field would have to invent a time for the all-day case — the exact inference
        /// ADR 0647 refuses. <see cref="AllDay"/> says which shape this is, so a client formats rather than
        /// guesses.
        /// </remarks>
        public string? Start { get; set; }

        public string? End { get; set; }

        public string? Location { get; set; }

        /// <summary>True when the entry is a day rather than a moment — <see cref="Start"/> carries no time.</summary>
        public bool AllDay { get; set; }

        /// <summary>
        /// The stored <c>RRULE</c> as text (<c>FREQ=WEEKLY;BYDAY=TU</c>), or null when the entry does not repeat.
        /// </summary>
        /// <remarks>
        /// <b>Opaque, and never expanded.</b> The server does not compute a recurrence set (the epic's decision),
        /// so <see cref="Start"/> is the FIRST occurrence's and nothing else. That makes this field the only
        /// thing standing between a grid and a quiet lie: an entry drawn once, in a month it actually occupies
        /// four times, looks exactly like an entry that happens once. A client shows a marker; it does not try
        /// to read the rule.
        /// </remarks>
        public string? Repeats { get; set; }
    }

    public class ContactListResource : HypermediaResource
    {
        public List<ContactEntryResource> Contacts { get; set; } = [];
    }

    public class AppointmentListResource : HypermediaResource
    {
        public List<AppointmentEntryResource> Appointments { get; set; } = [];
    }

    /// <summary>
    /// The contacts filed in this addressbook, with their index fields.
    /// </summary>
    /// <remarks>
    /// The same rel as the create, on the same address, distinguished by method — so a client that can read the
    /// collection can list it, while the POST keeps its own right. Whether New is offered rides on the
    /// collection's <c>CanCreateEntries</c>, since one rel cannot say "read yes, write no".
    /// </remarks>
    [HttpGet("contacts")]
    public Task<IActionResult> ListContacts(
        Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        ListAsync(
            documentId, WellKnownMaskIds.Addressbook, cursor, limit, cancellationToken,
            (id, name, field) => new ContactEntryResource
            {
                Id = id,
                Name = name,
                FullName = field("Full name"),
                Email = field("Email"),
                Phone = field("Phone"),
                Organization = field("Organization"),
                HasPhoto = field("Photo") is not null,
            },
            (entries, links) => new ContactListResource { Contacts = entries, Links = links });

    [HttpHead("contacts")]
    public async Task<IActionResult> HeadContacts(Guid documentId, CancellationToken cancellationToken) =>
        await RequireFolderAsync(documentId, WellKnownMaskIds.Addressbook, cancellationToken) is null
            ? NotFound()
            : await CanListAsync(documentId, cancellationToken) ? NoContent() : Forbid();

    /// <summary>The appointments filed in this calendar, with their index fields. See <see cref="ListContacts"/>.</summary>
    [HttpGet("appointments")]
    public Task<IActionResult> ListAppointments(
        Guid documentId, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        ListAsync(
            documentId, WellKnownMaskIds.Calendar, cursor, limit, cancellationToken,
            (id, name, field) => new AppointmentEntryResource
            {
                Id = id,
                Name = name,
                Start = field("Start"),
                End = field("End"),
                Location = field("Location"),
                // A day, not a moment: the indexed value carries no time at all (ADR 0647).
                AllDay = field("Start") is { } start && !start.Contains('T', StringComparison.Ordinal),
                Repeats = field("Repeats"),
            },
            (entries, links) => new AppointmentListResource { Appointments = entries, Links = links });

    [HttpHead("appointments")]
    public async Task<IActionResult> HeadAppointments(Guid documentId, CancellationToken cancellationToken) =>
        await RequireFolderAsync(documentId, WellKnownMaskIds.Calendar, cancellationToken) is null
            ? NotFound()
            : await CanListAsync(documentId, cancellationToken) ? NoContent() : Forbid();

    private async Task<bool> CanListAsync(Guid documentId, CancellationToken cancellationToken) =>
        (await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanSee;

    /// <summary>
    /// One listing for both typed collections: page the children, read their index fields, shape a row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written once with the two genuine differences — how a row is shaped and what the envelope is called —
    /// passed in as lambdas at the call site. Two copies of this would drift in exactly the way that matters
    /// here: the paging, the access check and the field lookup are the parts a reader never re-reads.
    /// </para>
    /// <para>
    /// <b>Field values are read BY NAME.</b> A document has one row per index field, so filtering only by
    /// document id returns an arbitrary one — which is how a vCard's UID was once overwritten with a phone
    /// number (#628).
    /// </para>
    /// <para>
    /// Values are fetched for the whole page in ONE query and assembled in memory, rather than as a correlated
    /// sub-query per field per row: four sub-queries times a page of fifty is the shape that turns a listing
    /// into a timeout, and it is the same "one read, many follows" economy the clients are held to (ADR 0557).
    /// </para>
    /// </remarks>
    private async Task<IActionResult> ListAsync<TEntry, TList>(
        Guid documentId,
        Guid folderMaskId,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken,
        Func<Guid, string, Func<string, string?>, TEntry> shape,
        Func<List<TEntry>, List<Link>, TList> envelope)
        where TEntry : HypermediaResource
        where TList : HypermediaResource
    {
        if (await RequireFolderAsync(documentId, folderMaskId, cancellationToken) is null)
        {
            return NotFound();
        }

        if (!await CanListAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);
        var query = _dbContext.Documents.Where(d => d.ParentId == documentId);
        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(d => d.CreatedAt > cursorCreatedAt || (d.CreatedAt == cursorCreatedAt && d.Id > cursorId));
        }

        // Filter and order on the ENTITY before projecting: EF cannot translate an order applied to an already
        // projected positional shape, and the failure is a 500 rather than a compile error.
        var fetched = await query
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Take(pageSize + 1)
            .Select(d => new { d.Id, d.Name, d.CreatedAt })
            .ToListAsync(cancellationToken);

        var page = fetched.Take(pageSize).ToList();
        var ids = page.Select(p => p.Id).ToList();

        var values = await _dbContext.FieldValues
            .Where(fv => ids.Contains(fv.DocumentId))
            .Join(
                _dbContext.FieldDefinitions,
                fv => fv.FieldDefinitionId,
                fd => fd.Id,
                (fv, fd) => new { fv.DocumentId, fd.Name, fv.Value })
            .ToListAsync(cancellationToken);

        var byDocument = values
            .GroupBy(v => v.DocumentId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase));

        var entries = new List<TEntry>();
        foreach (var row in page)
        {
            var fields = byDocument.GetValueOrDefault(row.Id) ?? [];
            var entry = shape(row.Id, row.Name, name => fields.GetValueOrDefault(name) is { Length: > 0 } v ? v : null);

            // The row's own address, so acting on it never reads the pane's loaded state (ADR 0559).
            entry.Links = [new Link("self", $"/api/documents/{row.Id}", "GET")];

            // A contact that HAS a picture also carries its address, so the row follows a rel rather than
            // composing one (ADR 0543) and never spends a request discovering there is nothing to show.
            if (entry is ContactEntryResource { HasPhoto: true })
            {
                entry.Links.Add(new Link("photo", DocumentContactCardController.PhotoHref(row.Id), "GET"));
            }

            // The structured appointment, on the ROW (ADR 0690): the detail pane shows the recorded zones, the
            // notes and the URL, none of which are index fields, so it must read the entry itself. Advertised
            // here rather than discovered through `self` because that would cost TWO requests per selection —
            // one to resolve the row, one to follow the rel — on the tab's most-used interaction (ADR 0557).
            if (entry is AppointmentEntryResource)
            {
                entry.Links.Add(new Link("appointment", $"/api/documents/{row.Id}/appointment", "GET"));
            }
            entries.Add(entry);
        }

        var links = new List<Link> { new("self", $"/api/documents/{documentId}", "GET") };
        if (fetched.Count > pageSize && page.Count > 0)
        {
            var last = page[^1];
            links.Add(new Link("next", $"{Request.Path}?cursor={Cursor.Encode(last.CreatedAt, last.Id)}&limit={pageSize}", "GET"));
        }

        return Ok(envelope(entries, links));
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

        if (actual == maskId)
        {
            return folder;
        }

        // Debug, not Warning: the caller gets a 404 it can act on, and a conforming client never asks — the
        // absent rel already said so (ADR 0626's boundary). It is logged at all because the interesting case is
        // a client that DID ask, which means its rel gating disagrees with this rule.
        _logger.LogDebug(
            "Refused a typed-item create on {DocumentId}: it wears {ActualMaskId}, the create needs {RequiredMaskId}",
            documentId, actual, maskId);
        return null;
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

        // A completed unit of work with its outcome. The UID is deliberately NOT logged: it is the correlation
        // key a DAV sync matches on, not a secret, but it is also the one field that identifies the item to an
        // external system — the document id is what an administrator here can act on.
        _logger.LogInformation(
            "Created {ContentType} {DocumentId} named {DocumentName} in collection {CollectionId}",
            contentType, document.Id, document.Name, folder.Id);

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
