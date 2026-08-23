using SimplArchive.Api.Errors;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// Base class for mail-routing errors (#703): the refusals around a Mailbox's address-claims list — who may
// write it, and which claims it may carry. Inherits from ApiException so the global handler translates it to
// an RFC 7807 response; concrete errors inherit from this so a caller can `catch (MailRoutingException)`.
public abstract class MailRoutingException : ApiException
{
    protected MailRoutingException(string errorCode, int statusCode, string message,
        IReadOnlyDictionary<string, object?>? extensions = null)
        : base(errorCode, statusCode, message, extensions)
    {
    }
}
