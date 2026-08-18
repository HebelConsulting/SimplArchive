namespace SimplArchive.Api.Lmtp;

/// <summary>The LMTP delivery listener's configuration (ADR 0628), bound from the <c>Lmtp</c> section.</summary>
/// <remarks>
/// <para>
/// <b>This listener must never be publicly reachable.</b> LMTP has no authentication of its own — RFC 2033
/// assumes the only thing talking to it is the MTA that already did the hostile-input work, on a private
/// network. The compose stack keeps it on the internal network and binds no host port; the Helm chart puts it
/// on a ClusterIP Service. Exposing it is equivalent to accepting unauthenticated mail from anyone.
/// </para>
/// <para>
/// Off by default, so an installation that has not deliberately set up an MTA does not open a listener it did
/// not ask for.
/// </para>
/// </remarks>
public class LmtpOptions
{
    public bool Enabled { get; set; }

    /// <summary>The port the MTA delivers to. -1 binds an ephemeral port, which is what the E2E suite uses.</summary>
    public int Port { get; set; } = 2525;

    /// <summary>The address to bind. Loopback by default — deliberately the one that cannot be reached remotely.</summary>
    /// <remarks>
    /// A container deployment MUST widen this, because the MTA runs in its own container and cannot reach this
    /// one's loopback. That is the intended shape: opening the listener beyond the host is a decision an
    /// operator makes explicitly, having read why it must stay on a private network, rather than a default
    /// they inherit without noticing. Compose sets it to <c>0.0.0.0</c> on an internal network with no host
    /// port published.
    /// </remarks>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>The largest message accepted, in bytes. Beyond this the reply is a permanent 552.</summary>
    /// <remarks>
    /// A cap is required rather than optional: without one, a single sender decides how much memory the
    /// process uses. 35 MB is roughly what the common providers accept after base64 expansion.
    /// </remarks>
    public int MaxMessageBytes { get; set; } = 35 * 1024 * 1024;
}
