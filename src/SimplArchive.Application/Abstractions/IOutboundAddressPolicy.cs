using System.Net;

namespace SimplArchive.Application.Abstractions;

/// <summary>The reason a URL was refused, and whether it was refused at all (ADR 0717).</summary>
/// <remarks>
/// The reason is for the LOG and for the administrator who typed the URL — never for the caller of a delivery.
/// A refusal that says which address range it landed in is a network map handed out one probe at a time.
/// </remarks>
public readonly record struct OutboundUrlVerdict(bool Allowed, string Reason)
{
    public static OutboundUrlVerdict Allow { get; } = new(true, string.Empty);

    public static OutboundUrlVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Decides whether this installation may make an outbound HTTP request to a caller-supplied URL — the guard
/// against server-side request forgery on the two sinks that accept one: a tenant's audit webhook and a DAV
/// client's push endpoint (ADR 0717, superseding ADR 0126's blanket rule).
/// </summary>
/// <remarks>
/// Two questions rather than one, because they are asked at different moments. <see cref="ValidateAsync"/> is
/// asked when a URL is REGISTERED, so the person typing it gets a clear refusal. <see cref="IsPermitted"/> is
/// asked again at the moment of CONNECTING, against the address actually resolved — which is the check that
/// holds, because a name that resolved publicly when it was saved can resolve privately when it is called.
/// </remarks>
public interface IOutboundAddressPolicy
{
    /// <summary>Whether a resolved address may be connected to.</summary>
    bool IsPermitted(IPAddress address);

    /// <summary>
    /// Whether a URL may be registered: its scheme, its shape, and every address its host resolves to. A host
    /// resolving to several addresses must have ALL of them permitted — one public answer beside a private one
    /// is the shape of a rebinding attack, not of a well-configured name.
    /// </summary>
    Task<OutboundUrlVerdict> ValidateAsync(string url, CancellationToken cancellationToken = default);
}
