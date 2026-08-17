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
