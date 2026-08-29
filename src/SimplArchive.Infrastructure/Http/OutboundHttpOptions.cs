namespace SimplArchive.Infrastructure.Http;

/// <summary>
/// What this installation may reach outbound (ADR 0717), bound from the <c>OutboundHttp</c> configuration
/// section.
/// </summary>
public sealed class OutboundHttpOptions
{
    public const string SectionName = "OutboundHttp";

    /// <summary>
    /// Networks, in CIDR form, that a caller-supplied URL may resolve into even though they are otherwise
    /// refused — an on-premises log collector at <c>10.0.0.0/8</c>, a self-hosted push service on the LAN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <b>deployment</b> configuration on purpose. A tenant administrator can register a webhook URL
    /// but cannot widen what the network permits; the person who opts in is the one who owns the network.
    /// </para>
    /// <para>
    /// It does not reach the cloud-metadata addresses, which stay refused however this is set. An installation
    /// that allowlists its whole private range has still not asked to hand out its instance credentials.
    /// </para>
    /// </remarks>
    public IList<string> AllowedNetworks { get; set; } = [];
}
