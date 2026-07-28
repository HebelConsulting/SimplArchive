namespace SimplArchive.Api.Errors.Exceptions.Audit;

// Base class for audit-trail errors (ADR "Audit trail — retention and purge"). Inherits from ApiException so the
// global handler translates it to an RFC 7807 response; concrete errors inherit from this so a caller can
// `catch (AuditException)`. See the exception-type principle in CLAUDE.md.
public abstract class AuditException : ApiException
{
    protected AuditException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
