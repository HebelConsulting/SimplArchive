using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Tenants;

/// <summary>
/// The tenant's mail-ingest keypair (#1335, ADR 0820): the certificate senders encrypt TO when mailing
/// documents into the archive, and the private key the LMTP boundary decrypts with. One active key per
/// tenant, minted lazily the first time something needs it (a generated profile, an enveloped delivery),
/// with the tenant's designated ingest address as the certificate's rfc822Name — which is how a sender's
/// mail client finds it. The private half is stored OpenBao-transit-protected (the TOTP-secret pattern);
/// core-held by owner decision, so the feature reaches sidecar-free installations too.
/// </summary>
public class TenantIngestKey : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The public half, canonical PEM — what profiles embed and dialogs offer.</summary>
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>The PKCS#8 private key, transit-protected (plaintext-in-column only when no transit
    /// encryptor is configured — the NullTransitEncryptor deployment, same trade as TOTP secrets).</summary>
    public string PrivateKeyProtected { get; set; } = string.Empty;

    /// <summary>The rfc822Name the certificate was minted for — recorded so a changed domain is visible
    /// as a mismatch instead of a mystery.</summary>
    public string IngestAddress { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    // Tracked (the standing rule): an administrator will eventually rotate this from a dialog, and two
    // admins rotating concurrently is exactly the lost-update shape the token exists to refuse.
    public Guid ConcurrencyToken { get; set; }
}
