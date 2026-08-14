using SimplArchive.Api.Controllers;
using SimplArchive.Domain.Users;

namespace SimplArchive.Api.Users;

/// <summary>
/// The one place a <see cref="User"/>'s right columns and the wire-facing <see cref="SystemRights"/> shape map
/// onto each other — read, apply, and describe for the audit log.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>UsersController</c> as a #466 burn-down tranche, and the extraction is more than line
/// accounting: <c>GroupsController</c> was already reaching into <c>UsersController</c> for
/// <see cref="Describe"/>, which is the tell that this mapping never belonged to one controller. A right added
/// to the entity must appear in <b>all three</b> members here — keeping them adjacent in one file is what makes
/// a missed one visible in a diff.
/// </para>
/// </remarks>
public static class SystemRightsMapping
{
    /// <summary>The user's rights as the API reports them.</summary>
    public static SystemRights Read(User u) => new()
    {
        IsTenantAdmin = u.IsTenantAdmin,
        CanImpersonate = u.CanImpersonate,
        CanOverrideCheckout = u.CanOverrideCheckout,
        CanLegalHold = u.CanLegalHold,
        CanManageClassification = u.CanManageClassification,
        CanResetMfa = u.CanResetMfa,
        CanManageRepositories = u.CanManageRepositories,
        CanManageMasks = u.CanManageMasks,
        CanManageServiceAccounts = u.CanManageServiceAccounts,
        CanManageUsers = u.CanManageUsers,
        CanViewAuditLog = u.CanViewAuditLog,
        CanExport = u.CanExport,
        CanImport = u.CanImport,
        CanManageInboxes = u.CanManageInboxes,
        CanCreateExternalLink = u.CanCreateExternalLink,
        ClearanceRank = u.ClearanceRank,
    };

    /// <summary>Writes a rights request onto the user — the PUT is a full replacement, so every column is set.</summary>
    public static void Apply(User u, SystemRights r)
    {
        u.IsTenantAdmin = r.IsTenantAdmin;
        u.CanImpersonate = r.CanImpersonate;
        u.CanOverrideCheckout = r.CanOverrideCheckout;
        u.CanLegalHold = r.CanLegalHold;
        u.CanManageClassification = r.CanManageClassification;
        u.CanResetMfa = r.CanResetMfa;
        u.CanManageRepositories = r.CanManageRepositories;
        u.CanManageMasks = r.CanManageMasks;
        u.CanManageServiceAccounts = r.CanManageServiceAccounts;
        u.CanManageUsers = r.CanManageUsers;
        u.CanViewAuditLog = r.CanViewAuditLog;
        u.CanExport = r.CanExport;
        u.CanImport = r.CanImport;
        u.CanManageInboxes = r.CanManageInboxes;
        u.CanCreateExternalLink = r.CanCreateExternalLink;
        u.ClearanceRank = r.ClearanceRank;
    }

    /// <summary>A short summary of the enabled rights, for the audit Details column.</summary>
    public static string Describe(SystemRights r)
    {
        var names = new List<string>();
        if (r.IsTenantAdmin) names.Add("TenantAdmin");
        if (r.CanManageUsers) names.Add("ManageUsers");
        if (r.CanManageRepositories) names.Add("ManageRepositories");
        if (r.CanManageMasks) names.Add("ManageMasks");
        if (r.CanManageServiceAccounts) names.Add("ManageServiceAccounts");
        if (r.CanViewAuditLog) names.Add("ViewAuditLog");
        if (r.CanImpersonate) names.Add("Impersonate");
        if (r.CanOverrideCheckout) names.Add("OverrideCheckout");
        if (r.CanLegalHold) names.Add("LegalHold");
        if (r.CanManageClassification) names.Add("ManageClassification");
        if (r.CanResetMfa) names.Add("ResetMfa");
        if (r.CanExport) names.Add("Export");
        if (r.CanImport) names.Add("Import");
        if (r.ClearanceRank > 0) names.Add($"Clearance {r.ClearanceRank}");
        return names.Count == 0 ? "(no rights)" : string.Join(", ", names);
    }
}
