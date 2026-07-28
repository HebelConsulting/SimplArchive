namespace SimplArchive.Api.Errors.Exceptions.Principals;

// Base class for principal/identity-management errors — users, groups, service accounts, and platform
// administrators (ADRs "User/Group management endpoints", "ServiceAccount management endpoints", "Tenant
// onboarding and platform admin"). Inherits from ApiException so the global handler translates it to an RFC 7807
// response; concrete errors inherit from this so a caller can `catch (PrincipalException)`. See the exception-type
// principle in CLAUDE.md.
public abstract class PrincipalException : ApiException
{
    protected PrincipalException(string errorCode, int statusCode, string message)
        : base(errorCode, statusCode, message)
    {
    }
}
