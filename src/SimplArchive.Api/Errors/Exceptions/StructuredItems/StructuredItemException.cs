namespace SimplArchive.Api.Errors.Exceptions.StructuredItems;

// Base class for errors saving the RAW source of a structured item — a contact's vCard or an appointment's
// iCalendar (#648, ADR 0643). Inherits from ApiException so the global handler translates it to an RFC 7807
// response; concrete errors inherit from this so a caller can `catch (StructuredItemException)` for the whole
// area. See the exception-type principle in CLAUDE.md.
public abstract class StructuredItemException : ApiException
{
    protected StructuredItemException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
