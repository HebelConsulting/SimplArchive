using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Http;

/// <summary>
/// The one answer to "may we call this?" (ADR 0717), shared by every sink that accepts a caller-supplied URL.
/// Two copies of this answer would drift, and the copy nobody updated is the one an attacker finds.
/// </summary>
public sealed class OutboundAddressPolicy : IOutboundAddressPolicy
{
    // The link-local addresses cloud providers answer instance credentials on. Refused unconditionally —
    // an installation that allowlists its own private range has not thereby asked to hand out its identity.
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("169.254.169.254"),   // AWS / GCP / Azure / OpenStack
        IPAddress.Parse("fd00:ec2::254"),     // AWS, IPv6
    ];

    // Everything that is not the public internet. Deliberately wider than RFC 1918: carrier-grade NAT, the
    // "this network" block and the IPv6 equivalents are all addresses a request must not wander into by
    // accident, and each of them has been somebody's SSRF bypass.
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),         // "this network"
        IPNetwork.Parse("10.0.0.0/8"),        // RFC 1918
        IPNetwork.Parse("100.64.0.0/10"),     // RFC 6598 carrier-grade NAT
        IPNetwork.Parse("127.0.0.0/8"),       // loopback
        IPNetwork.Parse("169.254.0.0/16"),    // link-local, incl. the metadata addresses above
        IPNetwork.Parse("172.16.0.0/12"),     // RFC 1918
        IPNetwork.Parse("192.0.0.0/24"),      // IETF protocol assignments
        IPNetwork.Parse("192.168.0.0/16"),    // RFC 1918
        IPNetwork.Parse("198.18.0.0/15"),     // benchmarking
        IPNetwork.Parse("224.0.0.0/4"),       // multicast
        IPNetwork.Parse("240.0.0.0/4"),       // reserved, incl. 255.255.255.255
        IPNetwork.Parse("::/128"),            // unspecified
        IPNetwork.Parse("::1/128"),           // loopback
        IPNetwork.Parse("fc00::/7"),          // unique local
        IPNetwork.Parse("fe80::/10"),         // link-local
        IPNetwork.Parse("ff00::/8"),          // multicast
    ];

    private readonly IPNetwork[] _allowed;
    private readonly ILogger<OutboundAddressPolicy> _logger;

    public OutboundAddressPolicy(IOptions<OutboundHttpOptions> options, ILogger<OutboundAddressPolicy> logger)
    {
        _logger = logger;

        var parsed = new List<IPNetwork>();
        foreach (var entry in options.Value.AllowedNetworks ?? [])
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                parsed.Add(network);
            }
            else
            {
                // Named, not swallowed: an operator who mistyped a CIDR believes their collector is reachable,
                // and the symptom otherwise is a webhook that silently never delivers (ADR 0626).
                _logger.LogWarning(
                    "OutboundHttp:AllowedNetworks entry {Entry} is not a CIDR and is ignored; outbound requests to it stay refused",
                    entry);
            }
        }

        _allowed = [.. parsed];

        if (_allowed.Length > 0)
        {
            _logger.LogInformation(
                "Outbound requests may additionally reach {Count} configured network(s) that are otherwise refused", _allowed.Length);
        }
    }

    public bool IsPermitted(IPAddress address)
    {
        // An IPv4 address wearing an IPv6 coat is the oldest bypass in this family: ::ffff:127.0.0.1 is
        // loopback, and a check that only knows IPv4 ranges waves it through.
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (MetadataAddresses.Any(metadata => metadata.Equals(candidate)))
        {
            return false;
        }

        if (_allowed.Any(network => network.Contains(candidate)))
        {
            return true;
        }

        return !BlockedNetworks.Any(network => network.Contains(candidate));
    }

    public async Task<OutboundUrlVerdict> ValidateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return OutboundUrlVerdict.Refuse("not an absolute URL");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return OutboundUrlVerdict.Refuse($"scheme {uri.Scheme} is not http or https");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // Credentials in a URL are a smell in their own right, and they are how a target is disguised:
            // the part before the @ is what a person reads and not what the client connects to.
            return OutboundUrlVerdict.Refuse("the URL carries credentials");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            }
            catch (SocketException)
            {
                addresses = [];
            }
        }

        if (addresses.Length == 0)
        {
            // A name that does not resolve is ALLOWED to be registered, and that is not a hole: nothing can be
            // connected to it, and the check that matters runs again at connect time. Refusing here would make
            // saving a setting depend on DNS being up at that instant — and on the collector's name being
            // resolvable from wherever the administrator happens to be — while buying no security at all.
            return OutboundUrlVerdict.Allow;
        }

        // ALL, not any: a name answering with one public address beside one private address is the shape of a
        // rebinding attack, not of a well-configured name.
        return addresses.All(IsPermitted)
            ? OutboundUrlVerdict.Allow
            : OutboundUrlVerdict.Refuse("the host resolves to an address this installation may not call");
    }
}
