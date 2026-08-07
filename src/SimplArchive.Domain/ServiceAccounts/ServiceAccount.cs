using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.ServiceAccounts;

// A distinct principal type alongside User/Group (see ADR "Service account data model") — not a
// discriminator flag on User, since a service account has no email/password and its identity anchor is
// an OpenIddict application (client-credentials) instead. Create/rotate-secret/revoke are implemented —
// see ADR "ServiceAccount management endpoints".
public class ServiceAccount : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public required string OpenIddictApplicationClientId { get; set; }

    public bool IsActive { get; set; } = true;

    // Mirrors User.CanManageRepositories (ADR "System-level right grants") — User's system-level right
    // columns have no ServiceAccount equivalent by default; this one exists because repository creation
    // (ADR "Repository creation endpoint") needs a real gate for the ServiceAccount-only-caller reality
    // (no interactive User login flow exists yet). Not implied by anything else on ServiceAccount.
    public bool CanManageRepositories { get; set; }

    // Manage other users' inboxes (ADR 0532) — mirrors User.CanManageInboxes for a ServiceAccount caller.
    public bool CanManageInboxes { get; set; }

    // Mirrors User.CanManageMasks — built on both principals from the start this time, learning from
    // CanManageRepositories only getting a ServiceAccount mirror after the fact. See ADR "Mask creation
    // endpoint".
    public bool CanManageMasks { get; set; }

    // Gates SimplArchive.Api.Controllers.ServiceAccountsController's create/rotate-secret/revoke actions —
    // see ADR "ServiceAccount management endpoints". Mirrors User.CanManageServiceAccounts, same
    // independent-right pattern as CanManageRepositories/CanManageMasks above. The very first ServiceAccount
    // in a tenant still needs direct seeding — nothing to bootstrap this right from otherwise.
    public bool CanManageServiceAccounts { get; set; }

    // Gates SimplArchive.Api.Controllers.UsersController/GroupsController's create/update/delete actions
    // (including GroupMembership) — see ADR "User/Group management endpoints". Unlike the three rights
    // above, User has no CanManageUsers column of its own to mirror — Groups only exist in this system to
    // organize Users for ACL purposes, so one right covers managing both rather than splitting it in two.
    public bool CanManageUsers { get; set; }

    // Gates reading the audit log (ADR "Audit trail (first slice)") — mirrors User.CanViewAuditLog so a
    // ServiceAccount caller can be granted audit-read access.
    public bool CanViewAuditLog { get; set; }

    // Gate repository/folder export + import (ADR "Dedicated CanExport/CanImport rights") — mirrors User's
    // so a ServiceAccount (e.g. a backup/migration automation) can be granted export/import.
    public bool CanExport { get; set; }

    public bool CanImport { get; set; }

    // May create an external link, handing read access to people outside the system (ADR 0546). A dedicated right
    // rather than a reuse of CanReadContent: reading a document and publishing it to strangers are different acts.
    // Not backfilled for existing admins — it should be granted deliberately.
    public bool CanCreateExternalLink { get; set; }


    // Data-classification clearance (ADR "Sensitivity clearance enforcement"). A ServiceAccount can't belong to
    // a group, so its effective clearance is just this value. Default 0.
    public int ClearanceRank { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
