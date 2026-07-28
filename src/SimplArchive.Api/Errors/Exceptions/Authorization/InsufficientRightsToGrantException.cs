using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Authorization;

// The escalation cap: a caller may not grant rights beyond what it holds itself. Enforced identically across the
// ACL grant endpoint (ADR "ACL grant management endpoints"), the system-rights assignment endpoints (ADR
// "System-rights assignment + Users & groups tab"), and service-account creation (ADR "ServiceAccount management
// endpoints"). All four share the INSUFFICIENT_RIGHTS_TO_GRANT wire code; the static factories keep each throw
// site reading intent-first while preserving its context-specific detail message.
public sealed class InsufficientRightsToGrantException : AuthorizationException
{
    private InsufficientRightsToGrantException(string message)
        : base("INSUFFICIENT_RIGHTS_TO_GRANT", StatusCodes.Status403Forbidden, message)
    {
    }

    // A document-scoped ACL grant that exceeds the caller's own effective rights on that document.
    public static InsufficientRightsToGrantException OnDocument() =>
        new("Cannot grant rights beyond your own effective rights on this document.");

    // A service-account's rights bundle that exceeds the caller's own.
    public static InsufficientRightsToGrantException OnServiceAccount() =>
        new("Cannot grant rights beyond your own effective rights.");

    // A tenant-wide system-rights change: granting a right the caller lacks, or touching tenant-admin status
    // without being a tenant admin.
    public static InsufficientRightsToGrantException OnSystemRights() =>
        new("You cannot grant a right you do not hold yourself, or change tenant-admin status without being a tenant admin.");
}
