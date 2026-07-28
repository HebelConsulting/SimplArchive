using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Users;

// A one-time MFA recovery code (ADR "MFA (interactive login, TOTP)"), letting a user who has lost their
// authenticator still log in. Generated as a set at enroll and shown once; only the hash is stored. Single-use
// (UsedAt is set on consumption). ITenantScoped like its owning User; the login path verifies these before a
// tenant is established, so it ignores the tenant filter (same as the password lookup). Append/consume only —
// not versioned/soft-deletable.
public class UserRecoveryCode : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    // SHA-256 hex of the normalized code. Recovery codes are high-entropy random values, so an unsalted fast
    // hash is the standard choice (unlike passwords) and lets the login path match by direct lookup.
    public required string CodeHash { get; set; }

    // Null = unused; set to the instant it was redeemed (single-use).
    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
