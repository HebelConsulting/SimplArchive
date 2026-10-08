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

        // Fourth time, and the cheapest one to have got wrong: the upload paths catch
        // InvalidOperationException and report a name clash, so refusing a second version of a certificate
        // would have told the caller to rename a document whose name is not the problem — and would have
        // hidden the one sentence that says what to do instead (file a new enrolment).
        Domain.Documents.SingleVersionMaskException x =>
            new Errors.Exceptions.Documents.SingleVersionMaskException(x.Message),

        // The manuals feature's two refusals (ADRs 0891/0892): each says what to do instead, which a name clash
        // or a generic conflict would hide.
        Domain.Masks.CoreOwnedMaskException x =>
            new Errors.Exceptions.Documents.CoreOwnedMaskException(x.Message),
        Domain.Documents.StandardRepositoryProtectedException x =>
            new Errors.Exceptions.Documents.StandardRepositoryProtectedException(x.Message),

        // #1634: the field rules and the cycle rule had no type of their own, so every endpoint GUESSED between
        // "fill in the required field" and "this value is invalid" by what the request changed, and the create
        // endpoint reported both as a name clash. Typed now, they say what is wrong wherever they occur. The
        // NAME rule is deliberately not here: each endpoint words its own conflict (same parent, or target).
        Domain.Masks.MissingRequiredFieldException x =>
            new Errors.Exceptions.Documents.RequiredFieldMissingException(x.Message),
        Domain.Masks.FieldValueRejectedException x =>
            new Errors.Exceptions.Documents.FieldValueInvalidException(x.Message),
        Domain.Documents.DocumentParentCycleException =>
            new Errors.Exceptions.Documents.InvalidMoveTargetException(),

        _ => null,
    };
}
