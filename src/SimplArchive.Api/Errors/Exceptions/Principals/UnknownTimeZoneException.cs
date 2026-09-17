using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

/// <summary>
/// A display-timezone preference named a zone this host does not know (#1254).
/// </summary>
/// <remarks>
/// Refused rather than stored, because an unresolvable zone is not a preference — it is a value that will fail
/// silently at every render, and the user would have no way to tell that the setting they chose is doing
/// nothing. Clearing the preference is always available and is the way back to following their own device.
/// </remarks>
public sealed class UnknownTimeZoneException : PrincipalException
{
    public UnknownTimeZoneException(string timeZoneId)
        : base("UNKNOWN_TIME_ZONE", StatusCodes.Status400BadRequest,
            $"'{timeZoneId}' is not a time zone this server knows. Pick one from the list, or clear the "
            + "setting to follow your device's own zone.")
    {
    }
}
