using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimplArchive.Infrastructure.Http;

namespace SimplArchive.UnitTests;

// The outbound-address guard (ADR 0717, issue #845) — the answer both server-side-request-forgery sinks share:
// a tenant's audit webhook URL and a DAV client's push endpoint.
//
// Written against ADDRESSES rather than names wherever possible, so the suite does not depend on a resolver.
// The two cases that do use a name use "localhost", which every machine answers without a network.
public class OutboundAddressPolicyTests
{
    private static OutboundAddressPolicy Policy(params string[] allowed) =>
        new(
            Options.Create(new OutboundHttpOptions { AllowedNetworks = allowed }),
            NullLogger<OutboundAddressPolicy>.Instance);

    [Theory]
    [InlineData("127.0.0.1")]           // loopback
    [InlineData("127.1.2.3")]           // the whole 127/8, not just .0.1
    [InlineData("10.1.2.3")]            // RFC 1918
    [InlineData("172.20.0.5")]          // RFC 1918, the range most often got wrong
    [InlineData("192.168.1.1")]         // RFC 1918
    [InlineData("100.100.0.1")]         // carrier-grade NAT
    [InlineData("169.254.1.1")]         // link-local
    [InlineData("169.254.169.254")]     // the cloud metadata service
    [InlineData("0.0.0.0")]             // "this network"
    [InlineData("255.255.255.255")]     // broadcast
    [InlineData("::1")]                 // IPv6 loopback
    [InlineData("fe80::1")]             // IPv6 link-local
    [InlineData("fd12:3456::1")]        // IPv6 unique local
    public void Everything_that_is_not_the_public_internet_is_refused(string address) =>
        Assert.False(Policy().IsPermitted(IPAddress.Parse(address)));

    [Fact]
    public void An_ipv4_address_wearing_an_ipv6_coat_is_still_the_address_underneath()
    {
        // The oldest bypass in this family, and it is invisible in a range table: ::ffff:127.0.0.1 IS
        // loopback, and a check that only knows IPv4 ranges waves it through while looking correct.
        Assert.False(Policy().IsPermitted(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(Policy().IsPermitted(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.False(Policy().IsPermitted(IPAddress.Parse("::ffff:10.0.0.1")));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void A_public_address_is_permitted(string address) =>
        Assert.True(Policy().IsPermitted(IPAddress.Parse(address)));

    [Fact]
    public void An_allowlisted_network_permits_what_would_otherwise_be_refused()
    {
        // The on-premises case this exists for: a log collector at 10.20.30.40, reachable because whoever owns
        // the network said so in the DEPLOYMENT's configuration — not because a tenant administrator asked.
        var policy = Policy("10.20.0.0/16");

        Assert.True(policy.IsPermitted(IPAddress.Parse("10.20.30.40")));

        // …and only what was named. An allowlist is not a switch.
        Assert.False(policy.IsPermitted(IPAddress.Parse("10.21.0.1")));
        Assert.False(policy.IsPermitted(IPAddress.Parse("127.0.0.1")));
    }

    [Fact]
    public void The_metadata_addresses_stay_refused_however_wide_the_allowlist_is()
    {
        // An installation that allowlists its whole private range has not thereby asked to hand out its
        // instance credentials — which is what the metadata service answers, to anyone who can reach it.
        var policy = Policy("0.0.0.0/0", "::/0", "169.254.0.0/16");

        Assert.False(policy.IsPermitted(IPAddress.Parse("169.254.169.254")));
        Assert.False(policy.IsPermitted(IPAddress.Parse("fd00:ec2::254")));

        // The rest of that sweeping allowlist does work, so the assertion above is about the metadata
        // exception rather than about the allowlist being ignored.
        Assert.True(policy.IsPermitted(IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void A_mistyped_allowlist_entry_widens_nothing()
    {
        // "10.20.30.40/33" is not a network. Treating an unparseable entry as "allow it anyway" would turn a
        // typo into an open door; treating it as nothing keeps the default, and the constructor logs it.
        var policy = Policy("not-a-cidr", "10.20.30.40/33", "10.20.0.0/16");

        Assert.False(policy.IsPermitted(IPAddress.Parse("10.30.0.1")));
        Assert.True(policy.IsPermitted(IPAddress.Parse("10.20.0.1")));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/only")]
    [InlineData("ftp://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://user:secret@example.com/x")]
    [InlineData("http://127.0.0.1:9200/ingest")]
    [InlineData("http://[::1]:9200/ingest")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost:9200/ingest")]
    public async Task A_url_that_may_not_be_called_is_refused(string url) =>
        Assert.False((await Policy().ValidateAsync(url)).Allowed);

    [Fact]
    public async Task A_refusal_says_why_without_describing_the_network()
    {
        var verdict = await Policy().ValidateAsync("http://127.0.0.1/ingest");

        Assert.False(verdict.Allowed);
        Assert.NotEmpty(verdict.Reason);

        // The administrator has to be able to fix it, so the reason names the POLICY. It must not name the
        // address the host resolved to: that turns one refused save into a scanner with a readable output.
        Assert.DoesNotContain("127.0.0.1", verdict.Reason);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_may_still_be_registered()
    {
        // Deliberate, and not a hole: nothing can be connected to a name that does not resolve, and the check
        // that matters runs again at connect time. Refusing here would make saving a setting depend on DNS
        // being up at that instant, and on the collector's name being resolvable from the administrator's desk.
        var verdict = await Policy().ValidateAsync("https://collector.invalid/ingest");

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public async Task An_allowlisted_loopback_url_is_accepted()
    {
        // The end-to-end fixture's own configuration, in miniature: an operator with a receiver on loopback
        // says so, and the URL then passes — including via the name, which resolves to both families.
        var policy = Policy("127.0.0.0/8", "::1/128");

        Assert.True((await policy.ValidateAsync("http://127.0.0.1:9200/ingest")).Allowed);
        Assert.True((await policy.ValidateAsync("http://localhost:9200/ingest")).Allowed);
    }
}
