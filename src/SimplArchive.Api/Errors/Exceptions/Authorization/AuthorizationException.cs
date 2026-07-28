namespace SimplArchive.Api.Errors.Exceptions.Authorization;

// Base class for cross-cutting authorization errors that aren't tied to a single domain area — e.g. the
// escalation cap ("you can't hand out a right you don't hold yourself"), which is enforced identically by the
// ACL, users, groups, and service-account controllers. Inherits from ApiException so the global handler
// translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (AuthorizationException)`. See the exception-type principle in CLAUDE.md.
public abstract class AuthorizationException : ApiException
{
    protected AuthorizationException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
