namespace SimplArchive.Api.Errors.Exceptions.Encryption;

// Base class for encryption-feature errors (#1332: the self-service S/MIME certificate; later the at-rest
// legs). Inherits from ApiException so the global handler translates it to an RFC 7807 response; concrete
// errors inherit from this so a caller can `catch (EncryptionException)` for the whole area.
public abstract class EncryptionException : ApiException
{
    protected EncryptionException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
