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
}
