namespace SimplArchive.Api.Errors.Exceptions.Annotations;

// Base class for document sticky-note annotation errors (ADR "Document annotations"). Inherits from ApiException
// so the global handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (AnnotationException)` for the whole area. See the exception-type principle in CLAUDE.md.
public abstract class AnnotationException : ApiException
{
    protected AnnotationException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
