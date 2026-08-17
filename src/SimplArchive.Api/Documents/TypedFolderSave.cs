using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Saves document changes with the typed-folder refusal translated into its own API error (#562/#564).
/// </summary>
/// <remarks>
/// <para>
/// Every document-writing endpoint wraps its save in <c>catch (InvalidOperationException)</c> and rethrows a
/// name conflict, because for years that was the only invariant those paths could trip. It is not any more:
/// <c>SaveChanges</c> throws that one type for five distinct invariants, and typed-folder containment is the
/// newest. The result was a 409 saying "a document with this name already exists" for a name that was a fresh
/// GUID — a specific, checkable, false cause, which is worse than no message at all.
/// </para>
/// <para>
/// Translating here rather than at each call site means the existing catches stay exactly as they are: the
/// API exception derives from <c>ApiException</c>, not <c>InvalidOperationException</c>, so it passes straight
/// through them. One helper, and a site opts in by calling this instead of <c>SaveChangesAsync</c>.
/// </para>
/// </remarks>
public static class TypedFolderSave
{
    public static async Task SaveTranslatingContainmentAsync(
        this SimplArchiveDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Domain.Masks.TypedFolderContainmentException e)
        {
            throw new Errors.Exceptions.Documents.TypedFolderContainmentException(e.Message);
        }
    }
}
