using System.Net;

namespace SimplArchive.Api.Configuration;

/// <summary>
/// Reads the proxies a deployment says it trusts for <c>X-Forwarded-*</c> (#847, A05).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a type rather than two lines in <c>Program.cs</c>.</b> Two callers have to agree about what
/// the configuration MEANS: the pipeline, which trusts what it parses, and the readiness gate, which refuses a
/// production deployment that named nothing. Written twice they would drift, and the drift fails in the worst
/// direction — the gate seeing a value the pipeline does not parse would report a deployment as safe while it
/// still trusts every peer.
/// </para>
/// <para>
/// <b>An unparseable entry is DROPPED, not fatal, and is why <see cref="Invalid"/> exists.</b> Refusing to
/// start on a typo would take a running installation down on a configuration edit that was trying to make it
/// safer, and silently ignoring one would leave an administrator believing they had named a proxy they had
/// not. So the pipeline keeps serving while the gate names the bad entry — and because an entry that parses to
/// nothing is indistinguishable from one that was never written, a deployment whose every entry is invalid is
/// refused exactly like one that named none.
/// </para>
/// <para>
/// <b>Deliberately <see cref="System.Net.IPNetwork"/>, which is not the only type of that name in scope.</b>
/// <c>Microsoft.AspNetCore.HttpOverrides</c> defines its own, and importing that namespace here makes every
/// mention ambiguous — so this file does not import it, and the one the forwarded-headers options actually
/// take is the one named.
/// </para>
/// </remarks>
public static class ProxyTrust
{
    /// <summary>Config key holding comma-separated proxy addresses (<c>10.1.2.3, ::1</c>).</summary>
    public const string ProxiesKey = "App:KnownProxies";

    /// <summary>Config key holding comma-separated CIDR networks (<c>10.0.0.0/8, fd00::/8</c>).</summary>
    public const string NetworksKey = "App:KnownProxyNetworks";

    /// <summary>The proxy addresses the deployment names, skipping anything that does not parse.</summary>
    public static IEnumerable<IPAddress> KnownProxies(IConfiguration configuration) =>
        Entries(configuration[ProxiesKey])
            .Select(e => IPAddress.TryParse(e, out var address) ? address : null)
            .OfType<IPAddress>();

    /// <summary>The networks the deployment names, skipping anything that does not parse.</summary>
    public static IEnumerable<IPNetwork> KnownNetworks(IConfiguration configuration) =>
        Entries(configuration[NetworksKey]).Select(Parse).OfType<IPNetwork>();

    /// <summary>Every entry that was written but could not be read, so a gate can name it.</summary>
    public static IEnumerable<string> Invalid(IConfiguration configuration)
    {
        foreach (var entry in Entries(configuration[ProxiesKey]).Where(e => !IPAddress.TryParse(e, out _)))
        {
            yield return $"{ProxiesKey}: '{entry}' is not an IP address";
        }

        foreach (var entry in Entries(configuration[NetworksKey]).Where(e => Parse(e) is null))
        {
            yield return $"{NetworksKey}: '{entry}' is not a CIDR network";
        }
    }

    /// <summary>Whether this deployment has named anything it trusts.</summary>
    public static bool NamesAnyProxy(IConfiguration configuration) =>
        KnownProxies(configuration).Any() || KnownNetworks(configuration).Any();

    // Comma-separated, because that is what a Helm value and an env var can carry; whitespace tolerated so a
    // list broken across lines in a values file still reads.
    private static IEnumerable<string> Entries(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // `IPNetwork.TryParse` accepts a prefix length wider than the address family allows on some runtimes, so
    // the length is checked here rather than trusted: a /64 written against an IPv4 address would otherwise
    // widen the trusted set far past what the administrator wrote.
    private static IPNetwork? Parse(string entry)
    {
        var slash = entry.IndexOf('/');
        if (slash <= 0
            || !IPAddress.TryParse(entry[..slash], out var address)
            || !int.TryParse(entry[(slash + 1)..], out var prefix))
        {
            return null;
        }

        var maximum = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefix < 0 || prefix > maximum ? null : new IPNetwork(address, prefix);
    }

    /// <summary>
    /// Honors <c>X-Forwarded-*</c> from the proxies this deployment names, when it asks for them.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than inline in <c>Program.cs</c>, for two reasons.</b> It belongs beside the keys it
    /// reads — "who do we believe" is one question and was becoming two halves in two files — and
    /// <c>Program.cs</c> is at the 1000-line limit, which is not an exception to take for a block that has a
    /// natural home (CLAUDE.md: a controller stays thin; infrastructure-specific code moves out).
    /// <b>It must still run FIRST in the pipeline</b>, before anything reads scheme or host.
    /// </remarks>
    public static void UseTrustedForwardedHeaders(WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("App:TrustProxyHeaders"))
        {
            var forwardedOptions = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
            {
                ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                    | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                    | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost,
            };

            // WHO we believe, and why it is worth naming (#847, A05). These headers decide the scheme and host every
            // generated URL carries and the address the credential throttle counts against (ADR 0716) — so whoever
            // can set them can make the app mint links to a host it does not serve, and can make a brute-force attempt
            // look like it came from somewhere else. Trusting ANY peer is therefore a real hole wherever the app is
            // reachable by anything but its proxy.
            //
            // Named proxies narrow it to the hop that is actually in front. Empty lists are the DEV posture and stay
            // the default for the LAN-testing stack this was built for (ADR 0473) and for Compose, where the Api is
            // only ever reached through Caddy — and outside Development the readiness gate refuses that combination
            // rather than letting it pass unnoticed (ProductionReadinessValidator).
            var proxies = ProxyTrust.KnownProxies(app.Configuration).ToList();
            var networks = ProxyTrust.KnownNetworks(app.Configuration).ToList();

            forwardedOptions.KnownIPNetworks.Clear();
            forwardedOptions.KnownProxies.Clear();
            foreach (var proxy in proxies)
            {
                forwardedOptions.KnownProxies.Add(proxy);
            }

            foreach (var network in networks)
            {
                forwardedOptions.KnownIPNetworks.Add(network);
            }

            if (proxies.Count > 0 || networks.Count > 0)
            {
                app.Logger.LogInformation(
                    "Forwarded headers are honored from {ProxyCount} named proxy address(es) and {NetworkCount} "
                    + "network(s); anything else is ignored.", proxies.Count, networks.Count);
            }
            else
            {
                // Named loudly rather than silently: a reader of the log should not have to infer the posture from the
                // ABSENCE of the line above (ADR 0626 — a degraded capability the caller cannot see says so).
                app.Logger.LogWarning(
                    "Forwarded headers are honored from ANY peer: neither App:KnownProxies nor App:KnownProxyNetworks "
                    + "is set. That is the development posture — whoever can reach this process can choose the host "
                    + "its links carry and the address the sign-in throttle counts. Outside Development the readiness "
                    + "gate refuses it.");
            }

            app.UseForwardedHeaders(forwardedOptions);
        }
    }
}
