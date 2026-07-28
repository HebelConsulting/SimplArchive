using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Users;

// A registered WebAuthn/passkey credential for a User (ADR "WebAuthn passkeys as a second factor"). Used as a
// second factor at the login challenge (alongside TOTP / recovery codes). ITenantScoped; append/remove only
// (not versioned/soft-deletable) — SignCount/LastUsedAt are updated on each successful assertion.
public class WebAuthnCredential : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The owning user; a passkey belongs to exactly one user (a ServiceAccount has no interactive login).
    public Guid UserId { get; set; }

    // The raw WebAuthn credential id (returned by the authenticator) — globally unique; the assertion identifies
    // the credential by this.
    public required byte[] CredentialId { get; set; }

    // The COSE-encoded public key used to verify assertion signatures.
    public required byte[] PublicKey { get; set; }

    // The authenticator's signature counter — a decrease signals a cloned authenticator (rejected).
    public long SignCount { get; set; }

    // The authenticator model identifier (all-zero when the authenticator doesn't disclose one).
    public Guid AaGuid { get; set; }

    // A user-friendly label ("MacBook Touch ID", "YubiKey 5").
    public required string Name { get; set; }

    // The authenticator's supported transports (comma-joined, e.g. "internal,hybrid"), optional.
    public string? Transports { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
