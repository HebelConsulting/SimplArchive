using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Masks;

/// <summary>
/// Whether a document is a folder, from its mask's <c>IsFolderMask</c> (#1708): the same column
/// <see cref="MaskContainmentRules.IsFolderMask"/> reads, for code that holds one document rather than a page of rows
/// (the search indexer and its rebuild). Never "has no file version": a file-less record is a document.
/// </summary>
public static class FolderMaskOf
{
    /// <remarks>A document without a mask version (not created since #1240's backfill) is not a folder.</remarks>
    public static Task<bool> IsFolderAsync(this SimplArchiveDbContext db, Guid? maskVersionId, CancellationToken cancellationToken) =>
        db.MaskVersions.Where(mv => mv.Id == maskVersionId)
            .Select(mv => db.Masks.Where(m => m.Id == mv.MaskId).Select(m => m.IsFolderMask).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);
}
