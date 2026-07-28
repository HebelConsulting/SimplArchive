namespace SimplArchive.Api.Errors.Exceptions.Tags;

// Base class for tag-catalog errors (ADR "Tag controlled vocabulary"). See the exception-type principle in CLAUDE.md.
public abstract class TagException : ApiException
{
    protected TagException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
