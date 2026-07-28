namespace SimplArchive.Api.Errors.Exceptions.References;

// Base class for document-reference (shortcut) errors (ADR "Desktop drag-and-drop move and reference"). Inherits
// from ApiException so the global handler translates it to an RFC 7807 response; concrete errors inherit from this
// so a caller can `catch (ReferenceException)`. See the exception-type principle in CLAUDE.md.
public abstract class ReferenceException : ApiException
{
    protected ReferenceException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
