using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Serves a strict-tier version's content as a CMS envelope addressed to the reader (#1393, ADR 0828).
/// </summary>
/// <remarks>
/// <para>
/// A SIBLING controller on the versions route rather than another action on
/// <c>DocumentVersionsController</c>, which stands at 972 lines: an exception to the 1000-line rule is not
/// the author's to grant, and this is the recipe CLAUDE.md names for exactly that — five sibling controllers
/// already share the documents routes for the same reason.
/// </para>
/// <para>
/// <b>The Api serves these bytes, and that is the existing exception rather than a new one.</b> A presigned
/// URL addresses a stored object; "this document, enveloped to this reader" is not one — it is per-reader and
/// does not exist until computed. The same reasoning as the zip ENTRY stream and the strict-tier external
/// link (ADR 0827): a byte stream that is not a stored object cannot be presigned.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/versions")]
[Authorize]
public class DocumentVersionEnvelopedContentController(
    SimplArchiveDbContext dbContext,
    DocumentAccessService access,
    IDocumentPreviewService previews,
    StrictEnvelopeDelivery envelopes) : ControllerBase
{
    /// <summary>The version's content, enveloped. <c>inline</c> asks for what a viewer opens rather than saves.</summary>
    /// <remarks>
    /// <para>
    /// One route for both rels, because what differs between "open it" and "save it" is the DISPOSITION and
    /// not the bytes — except for a format no viewer can render, where <c>inline</c> resolves to the same
    /// rendition the plaintext preview would have used. That is the one asymmetry, and it is the same one the
    /// non-strict path has.
    /// </para>
    /// <para>
    /// Refuses in three ways, each distinguishable: 404 when there is no such confirmed version, 403 when the
    /// caller may not read the content, and 409 when this tenant envelopes and the caller has no usable
    /// certificate. Never plaintext.
    /// </para>
    /// </remarks>
    [HttpGet("{versionId:guid}/enveloped-content")]
    public async Task<IActionResult> Get(
        Guid documentId, Guid versionId, [FromQuery] bool inline, CancellationToken cancellationToken)
    {
        var version = await dbContext.DocumentVersions
            .Where(v => v.Id == versionId && v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed)
            .Select(v => new { v.ObjectKey })
            .FirstOrDefaultAsync(cancellationToken);

        if (version is null || string.IsNullOrEmpty(version.ObjectKey))
        {
            return NotFound();
        }

        if (!await access.CanReadContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // Asked in this order on purpose: a tenant that does NOT envelope has no business serving from here,
        // and answering 404 says so without hinting at what the route would do elsewhere.
        if (!await envelopes.AppliesAsync(cancellationToken))
        {
            return NotFound();
        }

        if (await envelopes.ReaderCertificateAsync(cancellationToken) is not { } certificate)
        {
            throw new Errors.Exceptions.Encryption.ContentCannotBeEnvelopedException();
        }

        var documentName = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => d.Name)
            .FirstAsync(cancellationToken);

        // For an inline read of a format no viewer renders, the RENDITION is what the reader wants — resolved
        // through the same service the plaintext path uses, so a strict tenant and an ordinary one show the
        // same document rather than two different ideas of it. It is generated server-side from plaintext,
        // which ADR 0825 permits: the boundary is never AT REST, not never in process.
        var objectKey = inline
            ? await previews.GetDisplayObjectKeyAsync(version.ObjectKey, cancellationToken)
            : version.ObjectKey;

        var extension = Path.GetExtension(objectKey);
        var fileName = string.IsNullOrEmpty(extension)
            ? documentName
            : Path.GetFileNameWithoutExtension(documentName) + extension;

        var enveloped = await envelopes.EnvelopeAsync(objectKey, fileName, certificate, cancellationToken);

        // .p7m and application/pkcs7-mime: what an S/MIME-capable reader opens. The desktop client decrypts it
        // with the card and renders the bytes it gets back (#1353).
        return File(enveloped, "application/pkcs7-mime", Path.GetFileNameWithoutExtension(fileName) + ".p7m");
    }

    /// <summary>The same headers with no body — the every-GET-has-a-HEAD convention.</summary>
    /// <remarks>
    /// It does NOT build the envelope: a HEAD that enveloped would do all the work and throw the answer away,
    /// on a route whose work is the expensive part. It answers whether the content COULD be served, which is
    /// what a HEAD is for here.
    /// </remarks>
    [HttpHead("{versionId:guid}/enveloped-content")]
    public async Task<IActionResult> Head(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.DocumentVersions
            .AnyAsync(v => v.Id == versionId && v.DocumentId == documentId
                && v.Status == DocumentVersionStatus.Confirmed, cancellationToken);

        if (!exists)
        {
            return NotFound();
        }

        if (!await access.CanReadContentAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        if (!await envelopes.AppliesAsync(cancellationToken))
        {
            return NotFound();
        }

        return await envelopes.ReaderCertificateAsync(cancellationToken) is null
            ? throw new Errors.Exceptions.Encryption.ContentCannotBeEnvelopedException()
            : NoContent();
    }
}
