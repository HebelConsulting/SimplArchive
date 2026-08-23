using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Users &amp; groups rights matrix: the checkbox labels, and the two directions between a row of
/// checkbox states and an <see cref="AdminClient.SystemRightsData"/>.
/// </summary>
/// <remarks>
/// <para>
/// Its own class because the three members are ONE fact stated three times — the labels, the index→right read
/// and the states→bundle write must agree on the same order, and a right added to one and not the others is a
/// checkbox that edits the wrong column. Adjacent in one small file, that is visible in a diff; spread through
/// a 7,000-line view-model it is not. Lifted out while adding CanAccessWithoutGrant (ADR 0670), which took
/// MainWindowViewModel.cs over its standing-debt ceiling by exactly one line — so the debt goes down rather
/// than up (issue #466).
/// </para>
/// <para>
/// The order is <see cref="AdminClient.SystemRightsData"/>'s constructor order, and must stay that way.
/// </para>
/// </remarks>
public static class SystemRightsMatrix
{
    public static readonly string[] Labels =
    [
        "Tenant administrator", "Impersonate", "Override checkout", "Legal hold",
        "Manage classification", "Reset MFA", "Manage repositories", "Manage masks",
        "Manage service accounts", "Manage users & groups", "View audit log", "Export", "Import",
        "Manage intrays", "Create external links", "Access without grant", "Manage mail routing",
    ];

    /// <summary>The right at a checkbox position.</summary>
    public static bool At(AdminClient.SystemRightsData r, int i) => i switch
    {
        0 => r.IsTenantAdmin,
        1 => r.CanImpersonate,
        2 => r.CanOverrideCheckout,
        3 => r.CanLegalHold,
        4 => r.CanManageClassification,
        5 => r.CanResetMfa,
        6 => r.CanManageRepositories,
        7 => r.CanManageMasks,
        8 => r.CanManageServiceAccounts,
        9 => r.CanManageUsers,
        10 => r.CanViewAuditLog,
        11 => r.CanExport,
        12 => r.CanImport,
        13 => r.CanManageIntrays,
        14 => r.CanCreateExternalLink,
        15 => r.CanAccessWithoutGrant,
        _ => r.CanManageMailRouting,
    };

    /// <summary>The checkbox row read back as a rights bundle, with the clearance that rides alongside it.</summary>
    public static AdminClient.SystemRightsData From(IReadOnlyList<PrincipalRightViewModel> rights, int clearanceRank) => new(
        rights[0].IsChecked, rights[1].IsChecked, rights[2].IsChecked, rights[3].IsChecked,
        rights[4].IsChecked, rights[5].IsChecked, rights[6].IsChecked, rights[7].IsChecked,
        rights[8].IsChecked, rights[9].IsChecked, rights[10].IsChecked, rights[11].IsChecked,
        rights[12].IsChecked, rights[13].IsChecked, rights[14].IsChecked, rights[15].IsChecked,
        rights[16].IsChecked,
        clearanceRank);
}
