using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Passkeys;

// Thrown when the short-lived registration challenge token has expired (ADR "WebAuthn / passkeys second factor").
public sealed class PasskeyChallengeExpiredException : PasskeyException
{
    public PasskeyChallengeExpiredException()
        : base("PASSKEY_CHALLENGE_EXPIRED", StatusCodes.Status400BadRequest, "The registration challenge expired. Please try again.")
    {
    }
}
