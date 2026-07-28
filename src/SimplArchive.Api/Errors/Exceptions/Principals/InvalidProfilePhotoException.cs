using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

// Thrown when an uploaded profile photo fails validation (ADR "User profile photo"). Defaults to the size message;
// the byte-parsing validator passes its own detail.
public sealed class InvalidProfilePhotoException : PrincipalException
{
    public InvalidProfilePhotoException(string message = "The photo is too large.")
        : base("INVALID_PROFILE_PHOTO", StatusCodes.Status400BadRequest, message)
    {
    }
}
