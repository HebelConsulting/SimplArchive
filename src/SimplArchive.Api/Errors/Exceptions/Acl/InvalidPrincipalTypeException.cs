using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Acl;

// Thrown when an ACL route's {principalType} segment isn't users/groups/service-accounts (ADR "ACL grant
// management endpoints").
public sealed class InvalidPrincipalTypeException : AclException
{
    public InvalidPrincipalTypeException(string principalType)
        : base("INVALID_PRINCIPAL_TYPE", StatusCodes.Status400BadRequest, $"Unknown principal type '{principalType}'.")
    {
    }
}
