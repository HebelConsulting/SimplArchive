using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Workflow;
using SimplArchive.Api.Controllers;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

public sealed record VersionRow(
    Guid Id, Guid DocumentId, DocumentVersionStatus Status, int? VersionNumber, string ObjectKey,
    string? Sha256Hash, DateTimeOffset CreatedAt, DateOnly DocumentDate, TimeOnly? DocumentTime,
    Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, string? OcrLanguages, string? Comment,
    OcrVerdict? OcrVerdict = null, bool? IsSigned = null);

/// <summary>
/// Builds a <see cref="DocumentVersionResource"/> — its links, its preview address and its creator's name.
/// </summary>
/// <remarks>
/// <para>
/// Lifted out of <c>DocumentVersionsController</c> (#1220). It is not the HTTP edge: it binds nothing,
/// authorizes nothing and decides no status code. It shapes a response, which is the growth CLAUDE.md says
/// belongs in a named class in the Api project rather than in the controller — and it was 128 lines of a file
/// that had reached 991 before this change and could not take another line.
/// </para>
/// <para>
/// It takes the three collaborators it genuinely used and nothing else, which is how we know the lift was
/// clean: no <c>Url</c>, no <c>Request</c>, no caller identity. A builder that needed those would have been
/// the HTTP edge wearing a different name.
/// </para>
/// </remarks>
public sealed class DocumentVersionResourceBuilder(
    SimplArchiveDbContext dbContext,
    IDocumentPreviewService previews,
    IObjectStorageClient storage,
    StrictEnvelopeDelivery envelopes)
{
    // The same window the upload URL uses. A presigned link is handed to a browser that is about to follow it,
    // so it wants to be short; fifteen minutes survives a slow render without leaving a usable URL in a log.
    private static readonly TimeSpan PresignedUrlExpiry = TimeSpan.FromMinutes(15);


    public async Task<DocumentVersionResource> BuildAsync(VersionRow version, string documentName, CancellationToken cancellationToken)
    {
        var links = new List<Link> { new("self", $"/api/documents/{version.DocumentId}/versions/{version.Id}", "GET") };

        // Declared here rather than inside the confirmed-version branch below, because the resource is built for
        // a pending version too and the answer — does this TENANT envelope? — is the same either way.
        var enveloping = false;
        var previewConverted = false;

        if (version.Status == DocumentVersionStatus.Confirmed)
        {
            // Name the download after Document.Name but with the *version object's* extension, so a version
            // whose type differs from the document name saves correctly — e.g. the searchable-PDF successor of
            // a `.tif` document downloads as `<name>.pdf` (ADR "Searchable PDF successor for TIFFs"), and an
            // email named after its subject (no extension) saves as `<subject>.eml` (ADR "Email
            // auto-classification"). Falls back to the raw name when the object key carries no extension.
            var objectExtension = Path.GetExtension(version.ObjectKey);
            var downloadFileName = string.IsNullOrEmpty(objectExtension)
                ? documentName
                : Path.GetFileNameWithoutExtension(documentName) + objectExtension;

            // THE STRICT TIER'S DELIVERY (#1393, ADR 0828). The seam refuses to presign, correctly — no URL
            // can be an envelope — so these two rels point at a core-served route that envelopes the bytes to
            // the reader instead. The REL NAMES are unchanged, which is the point of ADR 0543's "rel names,
            // not paths, are the compatibility surface": a client follows `download` and gets what this tenant
            // can give it.
            //
            // And they are emitted ONLY when this reader has a usable certificate. A rel that is always
            // present and sometimes fails is the lying affordance 0543 exists to prevent; absent, it means
            // exactly "not available to you, here, now", and the client offers enrolment instead of a button.
            enveloping = await envelopes.AppliesAsync(cancellationToken);
            var envelopeCertificate = enveloping
                ? await envelopes.ReaderCertificateAsync(cancellationToken)
                : null;

            if (envelopeCertificate is not null)
            {
                var enveloped = $"/api/documents/{version.DocumentId}/versions/{version.Id}/enveloped-content";
                links.Add(new Link("download", enveloped, "GET"));
                links.Add(new Link("preview", $"{enveloped}?inline=true", "GET"));
            }
            // No URL means the strict tier will not serve these bytes as plaintext (#1376), so the rel is
            // OMITTED rather than the resource failing — ADR 0543: a missing rel means "not available to you,
            // here, now", and the client disables the affordance instead of trying. The document's metadata
            // still renders, which is the whole reason the seam answers null rather than throwing.
            else if (await storage.GetPresignedDownloadUrlAsync(version.ObjectKey, PresignedUrlExpiry, downloadFileName, cancellationToken) is { } downloadUrl)
            {
                links.Add(new Link("download", downloadUrl.ToString(), "GET"));
            }

            // Inline-disposition URL the workbench preview renders in place — see ADR "Repositories
            // workbench UI". For formats the browser can't display (TIFF, office docs), this resolves to a
            // cached rendition instead of the original (ADR "Server-side preview renditions", "Office
            // document preview via Gotenberg"). Null when no viewable preview can be produced (conversion
            // failed / converter down) — omit the link so the client shows "No preview available" rather
            // than a blank pane (ADR "Preview fallback when a rendition can't be produced").
            // The preview rel was already added above for an enveloped reader — pointing at the same route with
            // an inline disposition, because what differs between "open it" and "save it" is the disposition
            // and not the bytes.
            if (envelopeCertificate is null
                && await previews.GetPreviewUrlAsync(version.ObjectKey, PresignedUrlExpiry, downloadFileName, cancellationToken) is { } preview)
            {
                links.Add(new Link("preview", preview.Url.ToString(), "GET"));
                previewConverted = preview.IsConverted;
            }

            // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"). A static link — the
            // endpoint computes/caches the layout on demand and returns 204 for formats with no overlay — so
            // building the resource stays cheap (no OCR/PDF parse here).
            links.Add(new Link("text-layout", $"/api/documents/{version.DocumentId}/versions/{version.Id}/text-layout", "GET"));

            // Ordered per-page image URLs for a multi-page TIFF (ADR "Multi-page TIFF preview pages"). Static
            // link; the endpoint returns 204 for every other format (the client then uses the single `preview`).
            links.Add(new Link("preview-pages", $"/api/documents/{version.DocumentId}/versions/{version.Id}/preview-pages", "GET"));

            // The version's approval workflow (ADR "Workflow / document state model", 0009). Static link — the
            // endpoint resolves the current status + valid-transition links on demand. The STATUS itself rides
            // in the payload (one indexed lookup, beside the preview resolution this method already pays) so a
            // client can label its workflow affordance without a request per rel (ADR 0557).
            links.Add(new Link("workflow", $"/api/documents/{version.DocumentId}/versions/{version.Id}/workflow", "GET"));

            // Roll back to this version (ADR "Version restore") — copies its content into a new current version.
            // Static link (the action enforces CanEditContent + the frozen/checked-out/workflow guards); the
            // client offers it for older versions.
            links.Add(new Link("restore", $"/api/documents/{version.DocumentId}/versions/{version.Id}/restore", "POST"));

            // Sticky notes / positional annotations pinned to this version's pages (ADR "Document annotations
            // (sticky notes)"). Static link — the endpoint lists them on demand.
            links.Add(new Link("annotations", $"/api/documents/{version.DocumentId}/versions/{version.Id}/annotations", "GET"));

            // This version's issuing date (ADR "System-field search"). Static like `restore` above — the PUT
            // enforces CanEditIndexData plus the frozen/checked-out guards, and resolving all of that per row
            // would put a rights + legal-hold + checkout lookup on every version in the list.
            links.Add(new Link("document-date", $"/api/documents/{version.DocumentId}/versions/{version.Id}/document-date", "PUT"));

            // Force a searchable-PDF successor from this version (#999's Make searchable): the user may
            // overrule the detector — a detector-blind scan, or a re-run with changed languages. CONDITIONAL
            // on what the enforcer refuses (ADR 0543): an OCR-candidate extension, and not signed (OCR would
            // break the signature). DocumentSearchableController is the enforcer of the same predicate.
            if (DocumentSearchableController.IsOcrCandidate(version.ObjectKey) && version.IsSigned != true)
            {
                links.Add(new Link("make-searchable", $"/api/documents/{version.DocumentId}/versions/{version.Id}/searchable", "PUT"));
            }
        }

        return new DocumentVersionResource
        {
            Id = version.Id,
            VersionNumber = version.VersionNumber,
            ObjectKey = version.ObjectKey,
            Sha256Hash = version.Sha256Hash,
            Status = version.Status.ToString(),
            WorkflowStatus = (await dbContext.WorkflowStates
                .Where(w => w.DocumentVersionId == version.Id)
                .Select(w => (WorkflowStatus?)w.Status)
                .FirstOrDefaultAsync(cancellationToken))?.ToString(),
            PreviewConverted = previewConverted,
            CreatedAt = version.CreatedAt,
            CreatedByName = await ResolveCreatorNameAsync(version.CreatedByUserId, version.CreatedByServiceAccountId, cancellationToken),
            DocumentDate = version.DocumentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DocumentTime = version.DocumentTime?.ToString("HH:mm", CultureInfo.InvariantCulture),
            OcrLanguages = version.OcrLanguages,
            FileExtension = Path.GetExtension(version.ObjectKey),
            Comment = version.Comment,
            OcrVerdict = version.OcrVerdict?.ToString(),
            IsSigned = version.IsSigned == true,
            // Stated rather than left to be inferred from the absent rels (#1352): a strict tenant's version
            // looks exactly like one whose rendition failed, and "no preview available" is the wrong
            // explanation for a browser that cannot open an envelope by construction.
            ContentIsEnveloped = enveloping,
            Links = links,
        };
    }

    private async Task<string> ResolveCreatorNameAsync(Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken)
    {
        if (userId is { } uid)
        {
            return await dbContext.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken) ?? "";
        }

        if (serviceAccountId is { } said)
        {
            return await dbContext.ServiceAccounts.Where(s => s.Id == said).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken) ?? "";
        }

        return "";
    }
}
