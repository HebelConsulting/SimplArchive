namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

// Area base for external-link errors (ADR 0546), so a caller can catch the whole family.
public abstract class ExternalLinkException : ApiException
{
    protected ExternalLinkException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
