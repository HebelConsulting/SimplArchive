using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

/// <summary>The address offered as a user's e-mail is not one (#465) — empty, or without an '@'.</summary>
/// <remarks>
/// Deliberately a shallow check rather than a pattern claiming to validate e-mail: the address is proven by
/// someone signing in with it, and a stricter regex here would refuse valid addresses while still admitting
/// unusable ones. This catches the typo that would otherwise be stored as a login identifier.
/// </remarks>
public sealed class InvalidUserEmailException : PrincipalException
{
    public InvalidUserEmailException(string offered)
        : base("USER_EMAIL_INVALID", StatusCodes.Status400BadRequest, $"'{offered}' is not an e-mail address.")
    {
    }
}
