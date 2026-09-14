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
        catch (Exception e) when (Translate(e) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// The API error for one of the domain refusals above, or <c>null</c> when this is not one of them.
    /// </summary>
    /// <remarks>
    /// Separate from the save so a caller that does NOT own its <c>SaveChangesAsync</c> can still translate.
    /// The combined detail save (ADR 0794) is exactly that: its commit belongs to the document's verb contract
    /// (ADR 0795), which owns the transaction and the precondition, so the only way for it to get these
    /// refusals right is to translate around the contract rather than inside a save of its own. Copying the
    /// three mappings there would be the drift the standing rule names — and it is the same bug the class
    /// comment above describes, which has already been made three times.
    /// </remarks>
    public static Exception? Translate(Exception e) => e switch
    {
        Domain.Masks.TypedFolderContainmentException x =>
            new Errors.Exceptions.Documents.TypedFolderContainmentException(x.Message),

        // Third time, same shape: the mask endpoint's own catch assumes a missing required field, so an
        // untranslated refusal here would tell the user to fill in a value that is not the problem.
        Domain.Masks.StructuralMaskImmutableException x =>
            new Errors.Exceptions.Documents.StructuralMaskImmutableException(x.Message),

        // Translated for the same reason as the line above, and it is the same bug twice: a caller that
        // catches InvalidOperationException wholesale reports whatever cause it happens to assume — which for
        // DocumentChildrenController is a name clash, on a name that is a fresh GUID.
        Domain.Documents.PersonalSpaceStructureException x =>
            new Errors.Exceptions.Documents.PersonalSpaceStructureException(x.Message),

        _ => null,
    };
}
