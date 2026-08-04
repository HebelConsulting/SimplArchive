using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Groups;

public class Group : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public Guid? ParentGroupId { get; set; }

    // Tenant-wide system-level rights, mirroring User's — see ADR "Users & groups administration tab". A
    // Group gains its own rights so the admin UI can assign the same rights matrix to a group as to a user.
    // ASSIGNABLE + STORED ONLY FOR NOW: these are not yet enforced (a member user's effective system rights
    // are still read straight off the User row) — flowing group rights down to members is a deferred
    // follow-up, the same "assignable but not-yet-enforced" status several of User's own rights already
    // have (CanImpersonate, CanLegalHold, etc.).
    public bool IsTenantAdmin { get; set; }

    public bool CanImpersonate { get; set; }

    public bool CanOverrideCheckout { get; set; }

    public bool CanLegalHold { get; set; }

    public bool CanManageClassification { get; set; }

    public bool CanResetMfa { get; set; }

    public bool CanManageRepositories { get; set; }

    // Manage other users' inboxes (ADR 0532) — flows to members like every other group system right.
    public bool CanManageInboxes { get; set; }

    public bool CanManageServiceAccounts { get; set; }

    public bool CanManageMasks { get; set; }

    public bool CanManageUsers { get; set; }

    // Gates reading the audit log (ADR "Audit trail (first slice)"). Mirrors User's, per the group-rights
    // parity of ADR "Users & groups administration tab" / "Enforce group system rights for members".
    public bool CanViewAuditLog { get; set; }

    // Gate repository/folder export + import (ADR "Dedicated CanExport/CanImport rights"). Mirror User's;
    // a member's effective rights are the union of their own and their groups' (ADR "Enforce group system
    // rights for members"), so a right held via a group takes effect.
    public bool CanExport { get; set; }

    public bool CanImport { get; set; }

    // Data-classification clearance conferred to members (ADR "Sensitivity clearance enforcement"). A member's
    // effective clearance is the max of their own and every effective group's ClearanceRank. Default 0.
    public int ClearanceRank { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
