namespace SimplArchive.Api.Errors.Exceptions.Passkeys;

// Base class for WebAuthn/passkey registration errors (ADR "WebAuthn / passkeys second factor"). Inherits from
// ApiException so the global handler translates it to an RFC 7807 response; concrete errors inherit from this so
// a caller can `catch (PasskeyException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class PasskeyException : ApiException
{
    protected PasskeyException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
