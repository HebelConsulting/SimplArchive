namespace SimplArchive.Auth;

// RFC 8693 token-exchange impersonation (ADR "User impersonation"). Shared between AddAuthServer (which allows
// the custom grant flow) and the Api's TokenController/middleware (which handle it).
public static class ImpersonationConstants
{
    // The RFC 8693 token-exchange grant type used on /connect/token to obtain an impersonation token.
    public const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    // Request parameter carrying the target User id to impersonate (a common RFC 8693 extension).
    public const string RequestedSubjectParameter = "requested_subject";

    // Claim on an impersonation token naming the impersonating (actor) User's id.
    public const string ImpersonatedByClaim = "impersonated_by";
}
