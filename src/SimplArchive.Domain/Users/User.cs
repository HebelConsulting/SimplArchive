using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Users;

public class User : ITenantScoped
{
    private string _email = null!;

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Email
    {
        get => _email;
        set
        {
            _email = value;
            NormalizedEmail = value.ToUpperInvariant();
        }
    }

    public string NormalizedEmail { get; private set; } = null!;

    public required string DisplayName { get; set; }

    // ASP.NET Core Identity's PasswordHasher<User> output (PBKDF2-based, framework-provided) — see ADR
    // "Interactive User login (foundation slice)". Nullable: a User can exist with no password set yet.
    // Never compare/store a raw password anywhere else.
    public string? PasswordHash { get; set; }

    // A separate, app-specific password for the WebDAV gateway (ADR "WebDAV gateway (read-write, first slice)")
    // — PasswordHasher<User>-hashed, generated once and shown to the user, so the real login password isn't
    // typed into the OS keychain (and MFA users, who may have no simple password, can still mount). Nullable:
    // null = WebDAV access disabled for this user; revoke = clear it.
    public string? WebDavPasswordHash { get; set; }

    public bool IsActive { get; set; } = true;

    // Bypasses the repository ACL layer entirely — see ADR "Tenant admin ACL bypass". Not a Role
    // entity/hierarchy; a single flag, matching this project's preference for dedicated rights over a
    // general-purpose role system.
    public bool IsTenantAdmin { get; set; }

    // System-level dedicated rights — see ADR "System-level right grants". Each is independent of
    // IsTenantAdmin and of every other one here; every ADR that introduced one of these explicitly
    // decided it's "not folded into general Admin."
    public bool CanImpersonate { get; set; }

    public bool CanOverrideCheckout { get; set; }

    public bool CanLegalHold { get; set; }

    public bool CanManageClassification { get; set; }

    public bool CanResetMfa { get; set; }

    public bool CanManageRepositories { get; set; }

    // Manage other users' intrayes (ADR 0532): see + move any user's intray items tenant-wide (a triage right). Not
    // implied by IsTenantAdmin, same as every other right here.
    public bool CanManageIntrayes { get; set; }

    public bool CanManageServiceAccounts { get; set; }

    // Mask management is tenant-wide structural configuration, same tier as CanManageRepositories — see
    // ADR "Mask creation endpoint". Not implied by IsTenantAdmin, same as every other right here.
    public bool CanManageMasks { get; set; }

    // Gates UsersController/GroupsController — see ADR "User support for ServiceAccount/User/Group/Mask
    // management endpoints". A real, independent column, not implied by IsTenantAdmin, same as every
    // other right here — ServiceAccount already had this right; User didn't until this ADR.
    public bool CanManageUsers { get; set; }

    // Gates reading the audit log (ADR "Audit trail (first slice)") — a dedicated right, not implied by
    // IsTenantAdmin, same as every other right here.
    public bool CanViewAuditLog { get; set; }

    // Gate repository/folder export + import (ADR "Dedicated CanExport/CanImport rights") — two dedicated
    // rights that replace the old tenant-admin-only gate, so bulk data movement can be delegated without full
    // admin. Not implied by IsTenantAdmin, same as every other right here.
    public bool CanExport { get; set; }

    public bool CanImport { get; set; }

    // May create an external link, handing read access to people outside the system (ADR 0546). A dedicated right
    // rather than a reuse of CanReadContent: reading a document and publishing it to strangers are different acts.
    // Not backfilled for existing admins — it should be granted deliberately.
    public bool CanCreateExternalLink { get; set; }


    // Data-classification clearance (ADR "Sensitivity clearance enforcement"): the highest sensitivity-label
    // Rank this user may access on their own. A user's *effective* clearance is the max of this and every group
    // they effectively belong to. Only gates access when the tenant's EnforceClearance is on; a tenant admin
    // bypasses it (like the ACL bypass). Default 0 = the lowest (unlabelled documents only).
    public int ClearanceRank { get; set; }

    // Two-factor authentication (ADR "MFA (interactive login, TOTP)"). TotpSecret is the base32 TOTP shared
    // secret (null = not enrolled); dev-grade plaintext for now (same posture as the OpenIddict dev certs —
    // encryption rides on the secrets-management epic). MfaEnabledAt null = MFA off or mid-enrollment (secret
    // set but not yet confirmed); set = enabled, so this user must pass a TOTP / recovery code at every login.
    public string? TotpSecret { get; set; }

    public DateTimeOffset? MfaEnabledAt { get; set; }

    // Straighten crooked scans arriving in this user's intray (#491, ADR 0576). A per-USER preference rather
    // than a tenant policy, because it is the person feeding the scanner who knows whether their scans come out
    // crooked — but stored server-side rather than in a client's local settings, because the Worker's backstop
    // sweep has to read it for items that arrived over WebDAV, where no client is involved at all.
    //
    // Defaults to true here and via a backfill default in its migration — deliberately NOT via HasDefaultValue
    // in the model: a store default makes the CLR default unstorable, because EF omits a property that equals
    // it on INSERT, so a user created with `false` would silently be written as `true`.
    public bool DeskewIntrayUploads { get; set; } = true;

    // Correct a page that arrived 90 or 180 degrees round. A SEPARATE setting from deskew above, because the
    // two cost differently: rotation on a PDF is only the page's /Rotate attribute, so it is lossless and may
    // run on PDFs as well as TIFFs, while deskew cannot happen without re-rendering. They used to share one
    // flag, and the TIFF-only gate deskew needs was silently inherited by rotation, which needs no such thing.
    //
    // Same reasons as its siblings for being per-user, stored here, and carrying no HasDefaultValue.
    public bool RotateIntrayUploads { get; set; } = true;

    // Cut an arriving batch scan into one item per document, at the Patch 3 separator sheets between them
    // (#492, ADR 0577). A sibling of the flag above in every respect — same reason it is per-user, same reason
    // it is stored here rather than in a client, and the same reason it carries no HasDefaultValue.
    public bool CutIntrayUploadsAtPatchCodes { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
