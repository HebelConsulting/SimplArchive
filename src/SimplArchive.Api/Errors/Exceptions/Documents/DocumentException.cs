namespace SimplArchive.Api.Errors.Exceptions.Documents;

// Base class for document-management errors — move/rename, name conflicts, mask + index-data, field validation,
// purge, WORM (ADRs across the Document area). Inherits from ApiException so the global handler translates it to
// an RFC 7807 response; concrete errors inherit from this so a caller can `catch (DocumentException)`. See the
// exception-type principle in CLAUDE.md.
public abstract class DocumentException : ApiException
{
    protected DocumentException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
