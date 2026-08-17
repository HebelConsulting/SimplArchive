namespace SimplArchive.Application.Abstractions;

/// <summary>
/// A tenant-wide system-level rights bundle — the 10 boolean rights carried by both <c>User</c> and
/// <c>Group</c>. See ADR "Enforce group system rights for members": a user's <em>effective</em> system
/// rights are the union of their own and every group they effectively belong to.
/// </summary>
public sealed record SystemRightsSet(
    bool IsTenantAdmin,
    bool CanImpersonate,
    bool CanOverrideCheckout,
    bool CanLegalHold,
    bool CanManageClassification,
    bool CanResetMfa,
    bool CanManageRepositories,
    bool CanManageMasks,
    bool CanManageServiceAccounts,
    bool CanManageUsers,
    bool CanViewAuditLog,
    bool CanExport,
    bool CanImport,
    bool CanManageIntrayes,

    // May share a document with someone who has no account (ADR 0546).
    bool CanCreateExternalLink)
{
    public static readonly SystemRightsSet None = new(false, false, false, false, false, false, false, false, false, false, false, false, false, false, false);

    // Boolean OR of every right — the union of two bundles.
    public SystemRightsSet Union(SystemRightsSet other) => new(
        IsTenantAdmin || other.IsTenantAdmin,
        CanImpersonate || other.CanImpersonate,
        CanOverrideCheckout || other.CanOverrideCheckout,
        CanLegalHold || other.CanLegalHold,
        CanManageClassification || other.CanManageClassification,
        CanResetMfa || other.CanResetMfa,
        CanManageRepositories || other.CanManageRepositories,
        CanManageMasks || other.CanManageMasks,
        CanManageServiceAccounts || other.CanManageServiceAccounts,
        CanManageUsers || other.CanManageUsers,
        CanViewAuditLog || other.CanViewAuditLog,
        CanExport || other.CanExport,
        CanImport || other.CanImport,
        CanManageIntrayes || other.CanManageIntrayes,
        CanCreateExternalLink || other.CanCreateExternalLink);
}
