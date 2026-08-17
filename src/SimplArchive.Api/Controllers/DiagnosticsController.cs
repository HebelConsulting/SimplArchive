using System.Xml.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Diagnostics;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Not a real business endpoint — proves the authentication foundation (validate token → resolve claims
/// → set accessors → respond) and the hypermedia envelope / Problem Details error format work end-to-end.
/// See ADR "ServiceAccount request authentication foundation", ADR "Hypermedia envelope and Problem
/// Details errors (foundation slice)". Removed or replaced once real authorized endpoints exist.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/diagnostics")]
public class DiagnosticsController : ControllerBase
{
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IUserSystemRightsResolver _userSystemRights;

    private readonly ICurrentImpersonationAccessor _currentImpersonationAccessor;

    public DiagnosticsController(
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        SimplArchiveDbContext dbContext,
        IUserSystemRightsResolver userSystemRights,
        ICurrentImpersonationAccessor currentImpersonationAccessor)
    {
        _currentTenantAccessor = currentTenantAccessor;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _dbContext = dbContext;
        _userSystemRights = userSystemRights;
        _currentImpersonationAccessor = currentImpersonationAccessor;
    }

    // Plain mutable properties with a parameterless constructor (implicit, since there's no other
    // constructor), not { get; init; } — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs that shape. [XmlElement(IsNullable = true)] is required for a nullable value
    // type (Guid?) to serialize at all; XmlSerializer inspects the declared type, not whether it's ever
    // actually null at runtime.
    public class WhoAmIResource : HypermediaResource
    {
        [XmlElement(IsNullable = true)]
        public Guid? TenantId { get; set; }

        [XmlElement(IsNullable = true)]
        public Guid? ServiceAccountId { get; set; }

        // Set when the caller authenticated via the interactive User login flow — see ADR "Interactive
        // User login (foundation slice)". The proof point for that whole ADR, same role this endpoint
        // played for the original ServiceAccount auth foundation.
        [XmlElement(IsNullable = true)]
        public Guid? UserId { get; set; }

        // The tenant's and (for a User caller) the user's display names — clients use these for the desktop's
        // local intray/temp folder path `~/SimplArchive/{TenantName}/{UserName}/…` (ADR "S3-backed inbox").
        // Returned as data only; the API still identifies principals by id.
        public string? TenantName { get; set; }

        public string? UserName { get; set; }

        // Whether the User caller is a tenant administrator — lets the desktop show admin-only actions (e.g.
        // the searchable-PDF backfill, ADR "Backfill searchable PDFs for existing TIFFs"). False for a
        // ServiceAccount / PlatformAdministrator caller.
        public bool IsTenantAdmin { get; set; }

        // Whether the User caller may manage users/groups — gates the clients' "Users & groups" tab (ADR
        // "Users & groups administration tab"). True for a tenant admin (provisioned with it) or any User
        // holding CanManageUsers; false for a ServiceAccount / PlatformAdministrator caller.
        public bool CanManageUsers { get; set; }

        // Whether the User caller may manage service accounts — gates the clients' service-accounts management UI
        // (create / rotate-secret / revoke). True for a tenant admin or a User holding CanManageServiceAccounts.
        public bool CanManageServiceAccounts { get; set; }

        // Whether the User caller has a profile photo — the clients show it (else initials) in the corner
        // alongside DisplayName (ADR "User profile photo"), fetched from GET /api/users/{id}/photo.
        public bool HasPhoto { get; set; }

        // Whether the User caller may view the audit log — gates the clients' "Audit" tab (ADR "Audit trail
        // (first slice)"). True for any User holding CanViewAuditLog (own or via a group); false for a
        // ServiceAccount / PlatformAdministrator caller.
        public bool CanViewAuditLog { get; set; }

        // Whether the User caller has two-factor auth enabled — the clients' account menu shows Enable vs
        // Disable MFA (ADR "MFA (interactive login, TOTP)").
        public bool MfaEnabled { get; set; }

        // Whether the User caller may reset another user's MFA — gates the "Reset MFA" admin action
        // (ADR "MFA (interactive login, TOTP)"). True for any User holding CanResetMfa (own or via a group).
        public bool CanResetMfa { get; set; }

        // Whether the User caller may place/release legal holds — gates the clients' legal-hold actions + view
        // (ADR "Legal hold & retention enforcement"). True for any User holding CanLegalHold (own or via a group).
        public bool CanLegalHold { get; set; }

        // Whether the User caller may manage records classification / retention — gates the clients' Retention
        // view (ADR "Retention policies (auto-disposition)"). True for any User holding CanManageClassification.
        public bool CanManageClassification { get; set; }

        // Whether the User caller may force-release (override) another user's check-out — gates the clients'
        // "Override check-out" action (ADR "Document check-out / check-in"). True for any User holding
        // CanOverrideCheckout (own or via a group).
        public bool CanOverrideCheckout { get; set; }

        // Gates the clients' "Impersonate" action (ADR "User impersonation"). True for a User holding
        // CanImpersonate (own or via a group).
        public bool CanImpersonate { get; set; }

        // Gate the clients' repository/folder Export… + Import… actions (ADR "Dedicated CanExport/CanImport
        // rights"). True for a User holding the respective right (own or via a group).
        public bool CanExport { get; set; }

        public bool CanImport { get; set; }

        // Whether the User caller may manage other users' intrayes (ADR 0532) — gates the clients' intray
        // user-picker / cross-user triage. True for a User holding CanManageIntrayes (own or via a group).
        public bool CanManageIntrayes { get; set; }

        // The acting admin's display name when this is an impersonation session (else null) — drives the
        // clients' impersonation banner.
        public string? ImpersonatedBy { get; set; }
    }

    [HttpGet("whoami")]
    [Authorize]
    public async Task<IActionResult> WhoAmI(CancellationToken cancellationToken)
    {
        string? tenantName = null;
        if (_currentTenantAccessor.TenantId is { } tenantId)
        {
            tenantName = await _dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleOrDefaultAsync(cancellationToken);
        }

        string? userName = null;
        var isTenantAdmin = false;
        var canManageUsers = false;
        var canManageServiceAccounts = false;
        var canViewAuditLog = false;
        var canResetMfa = false;
        var canLegalHold = false;
        var canManageClassification = false;
        var canOverrideCheckout = false;
        var canImpersonate = false;
        var canExport = false;
        var canImport = false;
        var canManageIntrayes = false;
        var hasPhoto = false;
        var mfaEnabled = false;
        if (_currentUserAccessor.UserId is { } userId)
        {
            var profile = await _dbContext.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.DisplayName, MfaEnabled = u.MfaEnabledAt != null })
                .SingleOrDefaultAsync(cancellationToken);
            userName = profile?.DisplayName;
            mfaEnabled = profile?.MfaEnabled ?? false;
            // Effective rights (own ∪ groups) so the clients reflect a right held via a group — ADR
            // "Enforce group system rights for members".
            var rights = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            isTenantAdmin = rights.IsTenantAdmin;
            canManageUsers = rights.CanManageUsers;
            canManageServiceAccounts = rights.CanManageServiceAccounts;
            canViewAuditLog = rights.CanViewAuditLog;
            canResetMfa = rights.CanResetMfa;
            canLegalHold = rights.CanLegalHold;
            canManageClassification = rights.CanManageClassification;
            canOverrideCheckout = rights.CanOverrideCheckout;
            canImpersonate = rights.CanImpersonate;
            canExport = rights.CanExport;
            canImport = rights.CanImport;
            canManageIntrayes = rights.CanManageIntrayes;
            hasPhoto = await _dbContext.UserProfilePhotos.AnyAsync(p => p.UserId == userId, cancellationToken);
        }

        // When this is an impersonation session, name the acting admin so the clients can show a banner
        // (ADR "User impersonation").
        string? impersonatedBy = null;
        if (_currentImpersonationAccessor.ImpersonatorUserId is { } impersonatorId)
        {
            impersonatedBy = await _dbContext.Users.Where(u => u.Id == impersonatorId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken);
        }

        return Ok(new WhoAmIResource
        {
            TenantId = _currentTenantAccessor.TenantId,
            ServiceAccountId = _currentServiceAccountAccessor.ServiceAccountId,
            UserId = _currentUserAccessor.UserId,
            TenantName = tenantName,
            UserName = userName,
            IsTenantAdmin = isTenantAdmin,
            CanManageUsers = canManageUsers,
            CanManageServiceAccounts = canManageServiceAccounts,
            CanViewAuditLog = canViewAuditLog,
            MfaEnabled = mfaEnabled,
            CanResetMfa = canResetMfa,
            CanLegalHold = canLegalHold,
            CanManageClassification = canManageClassification,
            CanOverrideCheckout = canOverrideCheckout,
            CanImpersonate = canImpersonate,
            CanExport = canExport,
            CanImport = canImport,
            CanManageIntrayes = canManageIntrayes,
            ImpersonatedBy = impersonatedBy,
            HasPhoto = hasPhoto,

            // The caller's own avatar, advertised only when there is one (issue #416). whoami is the resource
            // the app bar already holds, so the rel belongs here rather than making the layout compose
            // /users/{id}/photo from an id it happens to be carrying — and its presence is the same fact
            // HasPhoto states, arrived at by the server instead of inferred by two clients separately.
            Links = hasPhoto && _currentUserAccessor.UserId is { } photoUserId
                ? [new Link("self", Url.Action(nameof(WhoAmI))!, "GET"), new Link("photo", $"/api/users/{photoUserId}/photo", "GET")]
                : [new Link("self", Url.Action(nameof(WhoAmI))!, "GET")],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("whoami")]
    [Authorize]
    public IActionResult HeadWhoAmI()
    {
        return NoContent();
    }

    // Deliberately throws a known ApiException to prove the Problem Details path renders a specific
    // errorCode — see ADR "Hypermedia envelope and Problem Details errors (foundation slice)".
    [HttpGet("throw-known-error")]
    [AllowAnonymous]
    public IActionResult ThrowKnownError()
    {
        throw new DiagnosticErrorException();
    }

    // Deliberately throws an unhandled exception to prove the fallback INTERNAL_ERROR path.
    [HttpGet("throw-unhandled-error")]
    [AllowAnonymous]
    public IActionResult ThrowUnhandledError()
    {
        throw new InvalidOperationException("This is a deliberate unhandled diagnostic error.");
    }
}
