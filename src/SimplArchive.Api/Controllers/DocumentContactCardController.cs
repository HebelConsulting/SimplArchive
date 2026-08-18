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
/// The structured, editable view of a contact's vCard (#564; ported alongside SimplCalCon's ADR 0082).
/// </summary>
/// <remarks>
/// <para>
/// A sibling of the raw document content rather than a replacement for it: <c>GET</c> returns the modelled
/// fields, <c>PUT</c> merges them back into the stored card. The merge is
/// <see cref="IContactCardComposer"/>'s, which keeps every property the form does not model — a contact
/// authored on somebody's phone carries a photo, custom labels and extensions we never see, and rewriting
/// the card without them is data loss the user discovers days later on another device.
/// </para>
/// <para>
/// A save writes a <b>new version</b>, exactly as a DAV <c>PUT</c> or an upload does, so history, indexing,
/// WORM and retention all behave as they do for any other edit. There is no in-place mutation of a stored
/// object anywhere in this codebase and this does not introduce one.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/contact-card")]
[Authorize]
public class DocumentContactCardController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly IObjectStorageClient _storage;
    private readonly IContactCardComposer _composer;
    private readonly DocumentFinalizer _finalizer;
    private readonly ICurrentUserAccessor _currentUser;

    public DocumentContactCardController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        IContactCardComposer composer,
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

    // Plain mutable classes, not records — XmlSerializer (ADR "JSON/XML content negotiation").
    public class ContactFieldResource
    {
        public string Value { get; set; } = string.Empty;

        public string? Type { get; set; }
    }

    public class ContactAddressResource
    {
        public string? Type { get; set; }

        public string? Street { get; set; }

        public string? City { get; set; }

        public string? Region { get; set; }

        public string? PostalCode { get; set; }

        public string? Country { get; set; }
    }

    public class ContactCardResource : HypermediaResource
    {
        public string? FormattedName { get; set; }

        public string? GivenName { get; set; }

        public string? FamilyName { get; set; }

        public string? Organization { get; set; }

        public string? Title { get; set; }

        public List<ContactFieldResource> Emails { get; set; } = [];

        public List<ContactFieldResource> Phones { get; set; } = [];

        public List<ContactAddressResource> Addresses { get; set; } = [];

        public string? Birthday { get; set; }

        public string? Url { get; set; }

        public string? Note { get; set; }

        /// <summary>False when the caller may read the card but not save it — so the client disables Edit
        /// rather than offering an action the server will refuse.</summary>
        public bool CanEdit { get; set; }
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
        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanReadContent)
        {
            return Forbid();
        }

        var resolved = await ResolveCardAsync(documentId, cancellationToken);
        if (resolved is not var (document, version, blob))
        {
            return NotFound();
        }

        var card = _composer.Read(blob);
        var resource = ToResource(card);
        resource.CanEdit = (await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent;
        resource.Links.Add(new Link("self", $"/api/documents/{documentId}/contact-card", "GET"));
        resource.Links.Add(new Link("document", $"/api/documents/{documentId}", "GET"));

        // The DOCUMENT's token: a version is append-only and carries none, and a concurrent save moves the
        // document's, which is exactly the collision this needs to detect.
        Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
        _ = version;
        return body ? Ok(resource) : new EmptyResult();
    }

    [HttpPut]
    public async Task<IActionResult> Put(
        Guid documentId,
        [FromBody] ContactCardResource request,
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

        var resolved = await ResolveCardAsync(documentId, cancellationToken);
        if (resolved is not var (document, version, blob))
        {
            return NotFound();
        }

        if (ifMatch.Trim('"', 'W', '/') != document.ConcurrencyToken.ToString())
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
        }

        // The UID is the correlation key a later DAV PUT matches on, so it is taken from the STORED card and
        // never from the request: a client that sent a different one would fork the contact into a duplicate.
        var uid = _dbContext.FieldValues.IgnoreQueryFilters()
            .Where(f => f.DocumentId == documentId)
            .Select(f => f.Value)
            .FirstOrDefault() ?? documentId.ToString();

        var merged = _composer.Merge(blob, FromResource(request), uid);

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var storageFolderId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, now, storageFolderId, versionId, ".vcf");
        await _storage.PutObjectAsync(objectKey, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(merged)), "text/vcard");

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
        // CHECK constraint, and the finalizer is what re-extracts the index fields from the merged card.
        await _finalizer.FinalizeAsync(newVersion, cancellationToken);

        // Re-read: SaveChanges regenerated the document's token, and the client needs the new one to save again.
        Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
        return NoContent();
    }

    /// <summary>The document, its current version, and the version's bytes — or null if it is not a card.</summary>
    private async Task<(Document Document, DocumentVersion Version, string Blob)?> ResolveCardAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var version = await CurrentVersion.ResolveAsync(
            _dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        if (version is null || !version.ObjectKey.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using var stream = await _storage.GetObjectAsync(version.ObjectKey, cancellationToken);
        using var reader = new StreamReader(stream);
        return (document, version, await reader.ReadToEndAsync(cancellationToken));
    }

    private static ContactCardResource ToResource(ContactCard card) => new()
    {
        FormattedName = card.FormattedName,
        GivenName = card.GivenName,
        FamilyName = card.FamilyName,
        Organization = card.Organization,
        Title = card.Title,
        Emails = [.. card.Emails.Select(e => new ContactFieldResource { Value = e.Value, Type = e.Type })],
        Phones = [.. card.Phones.Select(p => new ContactFieldResource { Value = p.Value, Type = p.Type })],
        Addresses = [.. card.Addresses.Select(a => new ContactAddressResource
        {
            Type = a.Type, Street = a.Street, City = a.City, Region = a.Region, PostalCode = a.PostalCode, Country = a.Country,
        })],
        Birthday = card.Birthday,
        Url = card.Url,
        Note = card.Note,
    };

    private static ContactCard FromResource(ContactCardResource r) => new(
        r.FormattedName,
        r.GivenName,
        r.FamilyName,
        r.Organization,
        r.Title,
        [.. (r.Emails ?? []).Select(e => new ContactField(e.Value, e.Type))],
        [.. (r.Phones ?? []).Select(p => new ContactField(p.Value, p.Type))],
        [.. (r.Addresses ?? []).Select(a => new ContactAddress(a.Type, a.Street, a.City, a.Region, a.PostalCode, a.Country))],
        r.Birthday,
        r.Url,
        r.Note);
}
