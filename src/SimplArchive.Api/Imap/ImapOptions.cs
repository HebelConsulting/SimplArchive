namespace SimplArchive.Api.Imap;

// Binds from the "Imap" section (ADR "IMAP endpoint (read-only, first slice)"). The endpoint is OFF unless
// enabled — a TCP listener is infrastructure a deployment must opt into and expose deliberately.
public class ImapOptions
{
    public const string SectionName = "Imap";

    public bool Enabled { get; set; }

    /// <summary>Plaintext port (dev only — the production-readiness gate refuses it outside Development). 0 = off; -1 = ephemeral (tests).</summary>
    public int Port { get; set; }

    /// <summary>Implicit-TLS port (RFC 8314 style, default 993 when configured). 0 = off; -1 = ephemeral (tests).</summary>
    public int TlsPort { get; set; }

    // The TLS certificate, PEM-sourced like the OpenIddict certs (config/OpenBao in dev/test, a Let's Encrypt
    // PEM in production — ADR 0339's sourcing).
    public string? CertificatePem { get; set; }

    public string? CertificateKeyPem { get; set; }

    /// <summary>What the IMAP dialog shows as the server host — split-network deployments (compose: internal
    /// name vs localhost) mirror ObjectStorage's PublicServiceUrl idea.</summary>
    public string? PublicHost { get; set; }

    /// <summary>What the IMAP dialog shows as the ports, when the published ones differ from the bound ones.</summary>
    /// <remarks>
    /// <para>
    /// The same split as <see cref="PublicHost"/>, which existed while the ports did not — so the dialog named
    /// the right host and the WRONG port. A container binding 9993 and published as <c>993:9993</c> told users
    /// to connect to 9993, which nothing outside can reach, and the failure looked like a broken server rather
    /// than a wrong number (#682).
    /// </para>
    /// <para>
    /// Null falls back to the bound port, so a deployment that publishes what it binds needs no configuration
    /// and nothing changes for it. Only a deployment that REMAPS has a second fact to state, and it is the one
    /// that already knows the mapping.
    /// </para>
    /// <para>
    /// Deliberately not defaulted to 993: an unset value meaning "the standard port" would be a guess about
    /// somebody else's port mapping, and would state it as confidently as the bug did.
    /// </para>
    /// </remarks>
    public int? PublicPort { get; set; }

    public int? PublicTlsPort { get; set; }

    /// <summary>The plaintext port to SHOW a user, or null when plaintext is off.</summary>
    public int? AdvertisedPort => Port == 0 ? null : PublicPort ?? Port;

    /// <summary>The TLS port to SHOW a user, or null when TLS is off.</summary>
    /// <remarks>
    /// Here rather than inline where the dialog is built, because "which port does a user dial" is a question
    /// about the OPTIONS — both halves of the answer live on this object — and because it is the fact that was
    /// wrong (#682). A property can be tested; an expression inside a controller's projection cannot.
    /// </remarks>
    public int? AdvertisedTlsPort => TlsPort == 0 ? null : PublicTlsPort ?? TlsPort;

    /// <summary>Seconds an AUTHENTICATED session may sit between commands before autologout (ADR 0618).
    /// Default 1800 — RFC 3501's "SHOULD NOT be less than 30 minutes" floor.</summary>
    public int IdleTimeoutSeconds { get; set; } = 1800;

    /// <summary>Seconds a connection gets from accept to successful authentication before it is dropped —
    /// unauthenticated sockets are the cheapest to hold open, so they get the short leash (ADR 0618).</summary>
    public int PreAuthTimeoutSeconds { get; set; } = 60;

    /// <summary>Concurrent connections one user may hold. Apple Mail alone opens ~4–5; the default leaves
    /// room for several devices, 8 is the recommended value for small deployments (ADR 0618).</summary>
    public int MaxConnectionsPerUser { get; set; } = 16;

    /// <summary>Concurrent connections across all users — process protection; the excess connection gets a
    /// BYE at the greeting (ADR 0618).</summary>
    public int MaxConnections { get; set; } = 200;
}
