using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

// Thrown when the OpenIddict application backing a service account / platform administrator can't be found (a
// data-integrity failure). Both share the OPENIDDICT_APPLICATION_NOT_FOUND wire code; the factories preserve each
// site's message.
public sealed class OpenIddictApplicationNotFoundException : PrincipalException
{
    private OpenIddictApplicationNotFoundException(string message)
        : base("OPENIDDICT_APPLICATION_NOT_FOUND", StatusCodes.Status500InternalServerError, message)
    {
    }

    public static OpenIddictApplicationNotFoundException ForServiceAccount() =>
        new("The service account's OpenIddict application could not be found.");

    public static OpenIddictApplicationNotFoundException ForPlatformAdministrator() =>
        new("The platform administrator's OpenIddict application could not be found.");
}
