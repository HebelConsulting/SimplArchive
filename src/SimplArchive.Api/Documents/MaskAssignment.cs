using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The one rule assigning a mask depends on: a document is filed against a mask's CURRENT version, and a mask
/// with no current version is a refusal rather than a null.
/// </summary>
/// <remarks>
/// Deliberately a resolver and not a writer, unlike its siblings <see cref="IndexDataWriter"/>,
/// <see cref="TagSetWriter"/> and <see cref="OcrLanguageWriter"/>. Assigning a mask is
/// <c>document.MaskVersionId = id</c> — one line, with nothing a caller could get wrong — so a class wrapping
/// it would be exactly the wrapper-whose-only-content-is-the-difference the standing rule names. What IS worth
/// sharing is the lookup and its refusal, because that is the part a second call site would re-derive slightly
/// differently.
/// </remarks>
public static class MaskAssignment
{
    /// <summary>
    /// The current <c>MaskVersion</c> of <paramref name="maskId"/>, with the name for the audit line.
    /// Throws <see cref="MaskNotFoundException"/> when the mask has none.
    /// </summary>
    public static async Task<(Guid VersionId, string Name)> ResolveCurrentVersionAsync(
        SimplArchiveDbContext dbContext, Guid maskId, CancellationToken cancellationToken)
    {
        var mask = await dbContext.MaskVersions
            .Where(v => v.MaskId == maskId && v.IsCurrent)
            .Select(v => new { v.Id, v.Name })
            .SingleOrDefaultAsync(cancellationToken);

        if (mask is null)
        {
            throw new MaskNotFoundException();
        }

        return (mask.Id, mask.Name);
    }
}
