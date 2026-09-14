using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// The combined detail save (ADR 0794) was refused by a <c>SaveChanges</c> invariant that cannot be attributed
/// to one aspect of the request.
/// </summary>
/// <remarks>
/// Index-data validation and the mask's required-field rule both surface as the same
/// <see cref="InvalidOperationException"/>, so when a single request changed BOTH there is no honest way to
/// say which refused it. This carries the invariant's own message and says only what is known.
///
/// It exists because the alternative is worse than vague: guessing would tell somebody to fill in a required
/// field when their actual problem is a value that does not match its format — a specific, checkable, FALSE
/// cause. This codebase has been bitten by exactly that three times, which is why the containment refusals got
/// their own translations.
///
/// When the changed set names only one of the two, the precise exception is thrown instead.
/// </remarks>
public sealed class DetailSaveRefusedException : DocumentException
{
    public DetailSaveRefusedException(string message)
        : base("DETAIL_SAVE_REFUSED", StatusCodes.Status400BadRequest, message)
    {
    }
}
