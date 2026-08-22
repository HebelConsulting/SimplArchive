namespace SimplArchive.Api.Controllers;

/// <summary>
/// The tenant-wide system-level rights bundle shared by the User and Group resources and their
/// <c>PUT .../rights</c> request body — see ADR "Users &amp; groups administration tab". Plain mutable
/// class (XmlSerializer needs a parameterless constructor + settable properties, ADR "JSON/XML content
/// negotiation"). Mirrors the boolean columns on <c>User</c>/<c>Group</c> exactly.
/// </summary>
public class SystemRights
{
    public bool IsTenantAdmin { get; set; }

    public bool CanImpersonate { get; set; }

    public bool CanOverrideCheckout { get; set; }

    public bool CanLegalHold { get; set; }

    public bool CanManageClassification { get; set; }

    public bool CanResetMfa { get; set; }

    public bool CanManageRepositories { get; set; }

    public bool CanManageMasks { get; set; }

    public bool CanManageServiceAccounts { get; set; }

    public bool CanManageUsers { get; set; }

    public bool CanViewAuditLog { get; set; }

    public bool CanExport { get; set; }

    public bool CanImport { get; set; }

    // Manage other users' intrays (ADR 0532): see + move any user's intray items tenant-wide.
    public bool CanManageIntrays { get; set; }

    // Share a document outside the tenant via an expiring link (ADR 0546). Its own right rather than part of
    // reading: handing a document to someone with no account is a different act from opening it, and the people
    // trusted to do the second are not automatically trusted to do the first.
    public bool CanCreateExternalLink { get; set; }

    // See + read any document the holder holds no CanSee grant on, and nothing else (ADR 0670). What an
    // administrator keeps once the tenant-admin bypass stops applying inside another user's personal space —
    // and revocable, so an admin can honestly give up their x-ray.
    public bool CanAccessWithoutGrant { get; set; }

    // Data-classification clearance (ADR "Sensitivity clearance enforcement") — not a boolean right but carried
    // in the same bundle so the Users & groups tab sets it alongside the rights. 0 = lowest (unlabelled only).
    public int ClearanceRank { get; set; }
}

/// <summary>
/// The escalation cap for assigning system rights (ADR "Users &amp; groups administration tab"): a caller
/// may only <em>grant</em> (flip false→true) a right it holds itself, and any change to
/// <see cref="SystemRights.IsTenantAdmin"/> requires the caller to be a tenant admin. Revokes (true→false)
/// and unchanged rights are always allowed — same "revoking never escalates" reasoning as
/// AclEntriesController's no-cap-on-DELETE rule, and same "can't hand out more than you hold" philosophy as
/// ServiceAccountsController. Comparing against the <em>current</em> bundle (not a blanket subset check)
/// means a pre-existing right the caller lacks doesn't lock the whole bundle from being saved.
/// </summary>
public static class SystemRightsPolicy
{
    public static bool CanApply(SystemRights caller, SystemRights current, SystemRights proposed)
    {
        // IsTenantAdmin is admin-gated: any change (grant or revoke) requires the caller to be a tenant admin.
        if (current.IsTenantAdmin != proposed.IsTenantAdmin && !caller.IsTenantAdmin)
        {
            return false;
        }

        // Clearance (ADR "Sensitivity clearance enforcement"): raising a principal's clearance above its current
        // value requires the caller be a tenant admin (who bypasses clearance) or hold at least that clearance
        // themselves — "can't hand out more than you hold". Lowering/unchanged is always allowed.
        if (proposed.ClearanceRank > current.ClearanceRank && !caller.IsTenantAdmin && proposed.ClearanceRank > caller.ClearanceRank)
        {
            return false;
        }

        // Every other right: granting it (false→true) requires the caller to hold it.
        return IsGrantAllowed(caller.CanImpersonate, current.CanImpersonate, proposed.CanImpersonate)
            && IsGrantAllowed(caller.CanOverrideCheckout, current.CanOverrideCheckout, proposed.CanOverrideCheckout)
            && IsGrantAllowed(caller.CanLegalHold, current.CanLegalHold, proposed.CanLegalHold)
            && IsGrantAllowed(caller.CanManageClassification, current.CanManageClassification, proposed.CanManageClassification)
            && IsGrantAllowed(caller.CanResetMfa, current.CanResetMfa, proposed.CanResetMfa)
            && IsGrantAllowed(caller.CanManageRepositories, current.CanManageRepositories, proposed.CanManageRepositories)
            && IsGrantAllowed(caller.CanManageMasks, current.CanManageMasks, proposed.CanManageMasks)
            && IsGrantAllowed(caller.CanManageServiceAccounts, current.CanManageServiceAccounts, proposed.CanManageServiceAccounts)
            && IsGrantAllowed(caller.CanManageUsers, current.CanManageUsers, proposed.CanManageUsers)
            && IsGrantAllowed(caller.CanViewAuditLog, current.CanViewAuditLog, proposed.CanViewAuditLog)
            && IsGrantAllowed(caller.CanExport, current.CanExport, proposed.CanExport)
            && IsGrantAllowed(caller.CanImport, current.CanImport, proposed.CanImport)
            && IsGrantAllowed(caller.CanManageIntrays, current.CanManageIntrays, proposed.CanManageIntrays)
            && IsGrantAllowed(caller.CanCreateExternalLink, current.CanCreateExternalLink, proposed.CanCreateExternalLink)
            && IsGrantAllowed(caller.CanAccessWithoutGrant, current.CanAccessWithoutGrant, proposed.CanAccessWithoutGrant);
    }

    private static bool IsGrantAllowed(bool callerHolds, bool current, bool proposed)
        => !(proposed && !current) || callerHolds;
}
