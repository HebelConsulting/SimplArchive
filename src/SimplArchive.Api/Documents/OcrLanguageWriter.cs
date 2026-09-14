using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Errors.Exceptions.Ocr;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Sets the OCR languages a document's scanned content is converted with: validate the codes against the fixed
/// catalog, pick the version the conversion actually reads, and stamp them on it.
/// </summary>
/// <remarks>
/// Extracted from <c>DocumentMetadataController.SetOcrLanguages</c> unchanged, so ADR 0794's combined
/// <c>PUT .../detail</c> delegates rather than copies. APPLIES ONLY — it neither gates nor saves.
///
/// It returns the version it wrote, because the caller needs that id to enqueue the re-conversion AFTER the
/// commit. Handing the id back rather than enqueueing from in here is what lets the combined endpoint fire one
/// side effect for a save that touched several aspects, instead of one per aspect.
/// </remarks>
public class OcrLanguageWriter(SimplArchiveDbContext dbContext)
{
    /// <summary>
    /// Stages the change and returns the source version. Throws
    /// <see cref="UnknownOcrLanguageException"/> for a code outside the catalog and
    /// <see cref="NoOcrSourceVersionException"/> when the document has nothing OCR can read.
    /// </summary>
    public async Task<DocumentVersion> ApplyAsync(Guid documentId, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        // Validate every code against the fixed catalog, preserving the caller's order (Tesseract priority).
        var codes = languages.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        var known = OcrLanguages.Supported.Select(l => l.Code).ToHashSet(StringComparer.Ordinal);
        var unknown = codes.FirstOrDefault(c => !known.Contains(c));
        if (unknown is not null)
        {
            throw UnknownOcrLanguageException.Unsupported(unknown);
        }

        // The conversion source: the latest confirmed OCR-candidate version — TIFF or PDF since #999 (the
        // TIFF-only gate predated scanned-PDF support and was exactly why an image-only PDF could never get
        // its languages set). Signed versions are excluded: OCR would break the signature, so the affordance
        // is absent for them and this enforcer matches (ADR 0543).
        var sourceVersion = await dbContext.DocumentVersions
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed && v.IsSigned != true)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(v => v.ObjectKey.ToLower().EndsWith(".tif") || v.ObjectKey.ToLower().EndsWith(".tiff")
                || v.ObjectKey.ToLower().EndsWith(".pdf"), cancellationToken);

        if (sourceVersion is null)
        {
            throw new NoOcrSourceVersionException();
        }

        sourceVersion.OcrLanguages = codes.Count == 0 ? null : string.Join('+', codes);
        return sourceVersion;
    }
}
