namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Produces and caches a small picture of a document's first page (issue #476).
/// </summary>
/// <remarks>
/// Built for the external-link landing page, where the viewer has no account and no session: they need to see
/// that they were sent the right document before downloading anything. Deliberately generated when the LINK is
/// created rather than when it is opened, so the wait falls on the sharer — who is signed in and expects a
/// moment — instead of on a stranger staring at an empty card.
///
/// The raster comes from the OCR sidecar rather than from this process: the Api image is Alpine (musl), and the
/// usable PDF rasterisers ship glibc-only natives, so an in-process implementation would work in development and
/// fail inside the container.
/// </remarks>
public interface IDocumentThumbnailService
{
    /// <summary>
    /// Produces the thumbnail for a stored version and caches it beside the version's other derived artifacts,
    /// returning the page count when it could be determined. Null when this document has no thumbnail to give —
    /// an unsupported format, or a sidecar that is not configured or not answering.
    /// </summary>
    /// <remarks>
    /// Best-effort by design: the landing page shows the name and the buttons with no picture, exactly as it did
    /// before this existed. A share that failed because a thumbnail could not be drawn would be a poor trade.
    /// </remarks>
    Task<DocumentThumbnail?> EnsureThumbnailAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// A short-lived presigned URL for an already-generated thumbnail, or null when there is none cached.
    /// </summary>
    /// <remarks>
    /// Never generates. The read path is anonymous and must not be a lever for making the server do work — and a
    /// missing thumbnail is not an error there, it is simply a page without a picture.
    /// </remarks>
    Task<Uri?> GetThumbnailUrlAsync(string objectKey, TimeSpan expiry, CancellationToken cancellationToken = default);
}

/// <summary>The cached thumbnail's object key, and the source document's page count (null when unknown).</summary>
public sealed record DocumentThumbnail(string ObjectKey, int? PageCount);
