namespace SimplArchive.Api.Errors.Exceptions.Users;

// Base class for user-management errors (ADR "User/Group management endpoints", "Workflow review reassignment").
// Inherits from ApiException so the global handler translates it to an RFC 7807 response; concrete errors inherit
// from this so a caller can `catch (UsersException)` for the whole area. See the exception-type principle in
// CLAUDE.md.
public abstract class UsersException : ApiException
{
    protected UsersException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
