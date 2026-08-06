namespace SimplArchive.Api.Errors.Exceptions.Chat;

// Base class for document comment-thread errors (ADR "Document comment thread"). Inherits from ApiException so the
// global handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (ChatException)`. See the exception-type principle in CLAUDE.md.
public abstract class ChatException : ApiException
{
    protected ChatException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
