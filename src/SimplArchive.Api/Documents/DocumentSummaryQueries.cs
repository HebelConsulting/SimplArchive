using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

internal record DocumentSummaryRow(Guid Id, string Name, DateTimeOffset CreatedAt, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, bool OnLegalHold, Guid? CheckedOutByUserId, string? CheckedOutByName, string? LatestObjectKey, string? DocumentType, DateOnly? DocumentDate, long? SizeBytes, Guid? SensitivityLabelId, string? SensitivityLabelName, string? SensitivityLabelColor, int VersionCount, DateTimeOffset? VersionCreatedAt, Guid? MaskId, string? CreatedByName);

/// <summary>
/// The one projection of a document into a LIST ROW — its columns, its presentation booleans, its addresses.
/// </summary>
/// <remarks>
/// <para>
/// Shared because a REFERENCE is another appearance of a document, so a referenced row is the same row (#768,
/// #769). It was not: the references listing projected a stub — id, name and four booleans — so every
/// list-row column a reference should carry came back empty, and the contents list drew blank Type, Doc date,
/// Size and Tags cells for exactly those rows. The columns were never broken; only references were.
/// </para>
/// <para>
/// Two copies of this would drift, and the drift is invisible: a column added to one listing simply does not
/// appear in the other, which reads as missing data rather than as a missing projection. That is how the first
/// one went unnoticed.
/// </para>
/// </remarks>
internal static class DocumentSummaryQueries
{
    /// <summary>A page's tags, batched — one query however many rows, never one per row (ADR 0557).</summary>
    internal static async Task<Dictionary<Guid, List<string>>> TagsForAsync(
        SimplArchiveDbContext db, List<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        return (await db.DocumentTags
                .Where(t => documentIds.Contains(t.DocumentId))
                .OrderBy(t => t.Tag)
                .Select(t => new { t.DocumentId, t.Tag })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.DocumentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
    }

    /// <summary>Projects documents into their list rows. EF-translatable — composed into the caller's query.</summary>
    internal static IQueryable<DocumentSummaryRow> AsSummaryRows(this IQueryable<Document> documents, SimplArchiveDbContext db) =>
        documents.Select(d => new DocumentSummaryRow(
                d.Id,
                d.Name,
                d.CreatedAt,
                // Child document/subfolder OR a reference filed into it (issue #376) — "is anything filed
                // here", which is what the empty-folder tree glyph and the open/navigate tests want.
                db.Documents.Any(c => c.ParentId == d.Id)
                    || db.DocumentReferences.Any(x => x.ParentFolderId == d.Id),
                db.DocumentVersions.Any(v => v.DocumentId == d.Id),
                db.Documents.Any(c => c.ParentId == d.Id && !db.DocumentVersions.Any(v => v.DocumentId == c.Id)),
                db.DocumentReferences.Any(r => r.TargetDocumentId == d.Id),
                db.LegalHoldItems.Any(i => i.DocumentId == d.Id && db.LegalHolds.Any(h => h.Id == i.LegalHoldId && h.ReleasedAt == null)),
                d.CheckedOutByUserId,
                db.Users.Where(u => u.Id == d.CheckedOutByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                // The CURRENT version's object key — the pinned version if CurrentVersionId is set (issue #265),
                // else the latest confirmed. Its extension is the document's file type (Name is a bare stem, ADR
                // 0277), letting the client detect e.g. a .zip to browse.
                d.CurrentVersionId != null
                    ? db.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => v.ObjectKey).FirstOrDefault()
                    : db.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => v.ObjectKey).FirstOrDefault(),
                // List-row columns (ADR "List-row columns and sorting"): the assigned mask's name, and the CURRENT
                // version's document date + size (pointer-aware, issue #265).
                db.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => mv.Name).FirstOrDefault(),
                d.CurrentVersionId != null
                    ? db.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => (DateOnly?)v.DocumentDate).FirstOrDefault()
                    : db.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => (DateOnly?)v.DocumentDate).FirstOrDefault(),
                d.CurrentVersionId != null
                    ? db.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => v.SizeBytes).FirstOrDefault()
                    : db.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => v.SizeBytes).FirstOrDefault(),
                d.SensitivityLabelId,
                d.SensitivityLabelId == null ? null : db.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Name).FirstOrDefault(),
                d.SensitivityLabelId == null ? null : db.SensitivityLabelDefinitions.Where(l => l.Id == d.SensitivityLabelId).Select(l => l.Color).FirstOrDefault(),
                // Confirmed-version count — gates the desktop "Compare versions" action (needs >= 2).
                db.DocumentVersions.Count(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed),
                // The CURRENT version's CreatedAt (filing timestamp) — the "Created" contents-sort key (pointer-aware).
                d.CurrentVersionId != null
                    ? db.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault()
                    : db.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => (DateTimeOffset?)v.CreatedAt).FirstOrDefault(),
                // The assigned mask's ID, alongside the NAME two fields up. The name is for display and is
                // localised/renamable; the id is the stable thing a rule keys on — "Note Folder" became
                // "Notebook" without a single document moving precisely because the id did not change.
                db.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => (Guid?)mv.MaskId).FirstOrDefault(),
                // The owner column (#768): who filed the CURRENT version, falling back to who created the
                // document. The fallback is what makes the column answerable for a folder and for a document
                // whose versions predate the field — a blank owner would be the same defect this issue is about.
                db.Users
                    .Where(u => u.Id == ((d.CurrentVersionId != null
                        ? db.DocumentVersions.Where(v => v.Id == d.CurrentVersionId && v.DocumentId == d.Id).Select(v => (Guid?)v.CreatedByUserId).FirstOrDefault()
                        : db.DocumentVersions.Where(v => v.DocumentId == d.Id && v.Status == DocumentVersionStatus.Confirmed).OrderByDescending(v => v.VersionNumber).Select(v => (Guid?)v.CreatedByUserId).FirstOrDefault())
                        ?? d.CreatedByUserId))
                    .Select(u => u.DisplayName).FirstOrDefault()));
}
