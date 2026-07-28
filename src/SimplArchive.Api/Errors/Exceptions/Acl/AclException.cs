namespace SimplArchive.Api.Errors.Exceptions.Acl;

// Base class for document-ACL grant-management errors (ADR "ACL grant management endpoints"). Inherits from
// ApiException so the global handler translates it to an RFC 7807 response; concrete errors (e.g.
// InvalidPrincipalTypeException) inherit from this so a caller can `catch (AclException)` for the whole area.
// See the exception-type principle in CLAUDE.md.
public abstract class AclException : ApiException
{
    protected AclException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
