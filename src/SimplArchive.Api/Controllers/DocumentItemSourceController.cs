using System.Text;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.StructuredItems;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The RAW source behind a structured item — a contact's vCard, an appointment's iCalendar (#648, ADR 0643).
/// </summary>
/// <remarks>
/// <para>
/// The structured editors model a deliberate field set and the server's merge preserves everything else, so a
/// photo, a custom label, an <c>X-</c> extension or a <c>VALARM</c> survives an edit without ever appearing in
/// the form. That is right, and it leaves the user with no way to see — let alone change — what is being kept
/// on their behalf. This is that way.
/// </para>
/// <para>
/// <b>A save here REPLACES the stored item; it does not merge.</b> That is what "raw" has to mean: the text
/// shown IS the item, so deleting a line has to delete the property, and a merge would silently put it back
/// from the previous version — an editor that undoes the user's edit while reporting success. The structured
/// <c>PUT</c> beside it keeps merging, and remains the everyday path.
/// </para>
/// <para>
/// Two things are checked before anything is written, because replace-not-merge removes the safety net the
/// merge provided: the text must PARSE as the format it claims, and it must not change the <c>UID</c>. Both
/// refuse without writing, so the previous version is untouched — and a version is written on success anyway,
/// so a regretted raw edit is recoverable exactly like any other.
/// </para>
/// <para>
/// One implementation with the per-family difference arriving as a value at the call site (CLAUDE.md's generic
/// rule): a contact and an appointment differ here in nothing but the extension, the media type and the word
/// used in an error message. Even the UID lives at the same place — <c>UID:</c> is a property in both RFC 6350
/// and RFC 5545, folded the same way — so the one reader serves both.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public partial class DocumentItemSourceController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly IObjectStorageClient _storage;
    private readonly DocumentFinalizer _finalizer;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly ILogger<DocumentItemSourceController> _logger;

    public DocumentItemSourceController(
        SimplArchiveDbContext dbContext,
        DocumentAccessService access,
        IObjectStorageClient storage,
        DocumentFinalizer finalizer,
        ICurrentUserAccessor currentUser,
        ILogger<DocumentItemSourceController> logger)
    {
        _dbContext = dbContext;
        _access = access;
        _storage = storage;
        _finalizer = finalizer;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>What differs between the two families, and nothing else.</summary>
    /// <param name="Extension">The stored object's extension, which is also how the item is recognised.</param>
    /// <param name="ContentType">What the replacement is written as.</param>
    /// <param name="Format">Named in the parse refusal, so the message says what was expected.</param>
    /// <param name="Begin">The opening line every well-formed item of this family starts with.</param>
    private sealed record ItemFamily(string Extension, string ContentType, string Format, string Begin);

    private static readonly ItemFamily Contact = new(".vcf", "text/vcard", "vCard", "BEGIN:VCARD");

    private static readonly ItemFamily Appointment = new(".ics", "text/calendar", "iCalendar", "BEGIN:VCALENDAR");

    public class ItemSourceResource : HypermediaResource
    {
        /// <summary>The stored item, verbatim.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary><c>vCard</c> or <c>iCalendar</c> — what the editor should say it is showing.</summary>
        public string Format { get; set; } = string.Empty;

        /// <summary>False when the caller may read but not save, so the raw box opens read-only.</summary>
        public bool CanEdit { get; set; }
    }

    public class SaveItemSourceRequest
    {
        public string Text { get; set; } = string.Empty;
    }

    [HttpGet("contact-card/source")]
    public Task<IActionResult> GetContact(Guid documentId, CancellationToken cancellationToken) =>
        ReadAsync(documentId, Contact, body: true, cancellationToken);

    /// <summary>Same headers, no body — ASP.NET Core does not strip a GET body for HEAD.</summary>
    [HttpHead("contact-card/source")]
    public Task<IActionResult> HeadContact(Guid documentId, CancellationToken cancellationToken) =>
        ReadAsync(documentId, Contact, body: false, cancellationToken);

    [HttpPut("contact-card/source")]
    public Task<IActionResult> PutContact(
        Guid documentId,
        [FromBody] SaveItemSourceRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken) =>
        SaveAsync(documentId, Contact, request, ifMatch, cancellationToken);

    [HttpGet("appointment/source")]
    public Task<IActionResult> GetAppointment(Guid documentId, CancellationToken cancellationToken) =>
        ReadAsync(documentId, Appointment, body: true, cancellationToken);

    [HttpHead("appointment/source")]
    public Task<IActionResult> HeadAppointment(Guid documentId, CancellationToken cancellationToken) =>
        ReadAsync(documentId, Appointment, body: false, cancellationToken);

    [HttpPut("appointment/source")]
    public Task<IActionResult> PutAppointment(
        Guid documentId,
        [FromBody] SaveItemSourceRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken) =>
        SaveAsync(documentId, Appointment, request, ifMatch, cancellationToken);

    private async Task<IActionResult> ReadAsync(
        Guid documentId, ItemFamily family, bool body, CancellationToken cancellationToken)
    {
        var rights = await _access.GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return Forbid();
        }

        if (await ResolveAsync(documentId, family, cancellationToken) is not var (document, _, text))
        {
            return NotFound();
        }

        var resource = new ItemSourceResource { Text = text, Format = family.Format, CanEdit = rights.CanEditContent };
        resource.Links.Add(new Link("self", SourceHref(documentId, family), "GET"));
        resource.Links.Add(new Link("document", $"/api/documents/{documentId}", "GET"));

        // The DOCUMENT's token, as the structured editor uses: a version is append-only and carries none, and a
        // concurrent save moves the document's — which is exactly the collision this needs to detect. Shared
        // deliberately, so editing the same item through the form and through the raw box collide with each
        // other rather than each quietly winning.
        Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
        return body ? Ok(resource) : new EmptyResult();
    }

    private async Task<IActionResult> SaveAsync(
        Guid documentId,
        ItemFamily family,
        SaveItemSourceRequest request,
        string? ifMatch,
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

        if (await ResolveAsync(documentId, family, cancellationToken) is not var (document, _, storedText))
        {
            return NotFound();
        }

        if (ifMatch.Trim('"', 'W', '/') != document.ConcurrencyToken.ToString())
        {
            throw Errors.Exceptions.Concurrency.EtagMismatchException.ForDocument();
        }

        var text = Normalize(request.Text);
        if (!LooksLike(text, family))
        {
            // Warning, not Debug: the caller believes it saved something it did not, and an administrator
            // reading the log is the only one who can tell whether an integration is mangling the payload.
            // Trace carries the exchange (ADR 0626) — the text itself is never logged, since a card is personal
            // data rather than a diagnostic.
            _logger.LogWarning(
                "Refused a raw {Format} save on {DocumentId}: the text does not open with {Expected}. "
                + "Enable Trace on this source to see the exchange",
                family.Format, documentId, family.Begin);
            throw new UnparsableItemSourceException(family.Format);
        }

        // Absence is not a change: an item whose UID line was removed keeps the stored one, because the user
        // deleting a line they did not write is not the same act as asserting a different identity.
        var storedUid = UidOf(storedText);
        if (UidOf(text) is { } submittedUid && storedUid is { } existing
            && !string.Equals(submittedUid, existing, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refused a raw {Format} save on {DocumentId}: it changes the UID, which would duplicate the item "
                + "on every client that syncs it",
                family.Format, documentId);
            throw new ItemSourceUidChangedException();
        }

        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var storageFolderId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, now, storageFolderId, versionId, family.Extension);
        await _storage.PutObjectAsync(
            objectKey, new MemoryStream(Encoding.UTF8.GetBytes(text)), family.ContentType, cancellationToken);

        // Pending + the shared finalizer, never a hand-written Confirmed version: the status is guarded by a
        // CHECK constraint, and the finalizer is what re-extracts the index fields — including the UID, which
        // is why a raw edit that legitimately rewrites other properties stays correlatable.
        var version = new DocumentVersion
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
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _finalizer.FinalizeAsync(version, cancellationToken);

        // The content changed, so the token this shares with the structured editor must move — without it the
        // If-Match here is enforced and inert, and a race between two raw saves loses somebody's whole item
        // rather than one field. See StructuredItemVersioning.
        await StructuredItemVersioning.MarkContentChangedAsync(_dbContext, document, cancellationToken);

        _logger.LogInformation(
            "Replaced the raw {Format} of {DocumentId} with {Bytes} bytes as version {VersionId}",
            family.Format, documentId, Encoding.UTF8.GetByteCount(text), versionId);

        // Re-read: SaveChanges regenerated the document's token, and the client needs the new one to save again.
        Response.Headers.ETag = $"\"{document.ConcurrencyToken}\"";
        return NoContent();
    }

    /// <summary>The document, its current version and the version's bytes — or null if it is not of this family.</summary>
    private async Task<(Document Document, DocumentVersion Version, string Text)?> ResolveAsync(
        Guid documentId, ItemFamily family, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var version = await CurrentVersion.ResolveAsync(
            _dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        if (version is null || !version.ObjectKey.EndsWith(family.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using var stream = await _storage.GetObjectAsync(version.ObjectKey, cancellationToken);
        using var reader = new StreamReader(stream);
        return (document, version, await reader.ReadToEndAsync(cancellationToken));
    }

    internal static string SourceHref(Guid documentId, bool isContact) =>
        SourceHref(documentId, isContact ? Contact : Appointment);

    private static string SourceHref(Guid documentId, ItemFamily family) =>
        family == Contact
            ? $"/api/documents/{documentId}/contact-card/source"
            : $"/api/documents/{documentId}/appointment/source";

    /// <summary>
    /// CRLF line endings and a trailing newline, which both formats require on the wire.
    /// </summary>
    /// <remarks>
    /// A browser textarea hands back bare LF whatever it was given, so a card round-tripped through the raw
    /// editor would otherwise be stored with line endings no strict parser accepts — the editor corrupting
    /// every item that merely passed through it, including ones the user did not knowingly change.
    /// </remarks>
    private static string Normalize(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal)
            .TrimEnd('\r', '\n');
        return normalized.Length == 0 ? normalized : normalized + "\r\n";
    }

    /// <summary>A structural check, not a validator: does this open and close as the family it claims?</summary>
    /// <remarks>
    /// Deliberately shallow. A full parse would reject cards that real clients emit and readers accept, and
    /// this exists to catch the mistake a person makes in a text box — pasting the wrong thing, or deleting the
    /// wrapper — not to arbitrate conformance. What survives here is still read by the same composer the
    /// structured editor uses, so a card that is well-formed but odd behaves exactly as one that arrived by DAV.
    /// </remarks>
    private static bool LooksLike(string text, ItemFamily family) =>
        text.StartsWith(family.Begin, StringComparison.OrdinalIgnoreCase)
        && text.Contains(family.Begin.Replace("BEGIN:", "END:", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    /// <summary>The item's <c>UID</c>, or null when it carries none.</summary>
    /// <remarks>
    /// Unfolds first: both RFC 6350 and RFC 5545 allow a long value to continue on the next line prefixed by a
    /// space or tab, so a UID long enough to wrap would otherwise read as a different — truncated — value, and
    /// the change check would refuse a save that changed nothing.
    /// </remarks>
    private static string? UidOf(string text) =>
        UidPattern().Match(Unfold(text)) is { Success: true } match ? match.Groups[1].Value.Trim() : null;

    private static string Unfold(string text) => FoldPattern().Replace(text, string.Empty);

    [GeneratedRegex(@"\r?\n[ \t]")]
    private static partial Regex FoldPattern();

    [GeneratedRegex(@"^UID:(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex UidPattern();
}
