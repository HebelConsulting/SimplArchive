using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// Registering a mail domain, and proving it (#667, ADR 0692).
//
// Ingress mail resolves a recipient domain-first (ADR 0628), so an empty TenantMailDomains means every message
// is refused — which is what shipped: the delivery path was covered end to end and there was no supported way
// to switch it on. The only writer was the tests, and the kiosk ran on a hand-written INSERT.
//
// The DNS is stubbed at the factory: CI cannot publish a TXT record for a domain a test invented, and a check
// whose only implementation talks to the real resolver is one that cannot be exercised at all.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class TenantMailDomainsTests
{
    private readonly E2EApiFactory _factory;

    public TenantMailDomainsTests(E2EApiFactory factory) => _factory = factory;

    /// <summary>
    /// A user who may manage mail routing, whose own address is AT the domain under test.
    /// </summary>
    /// <remarks>
    /// The address matters: delivery resolves domain-first and then the local part to a user, so "is this
    /// domain accepted" is only observable through a recipient that exists. A test asking about a domain with
    /// no user at it gets "refused" for the wrong reason — which is exactly how this helper was written first.
    /// </remarks>
    private async Task<(HttpClient Client, Guid TenantId, string Domain, string Address)> RouterAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var domain = $"d{Guid.NewGuid():N}.example";
        var email = $"router@{domain}";
        // canViewAuditLog too: removing a domain is audited, and the test that asserts it reads the log back
        // as this same user.
        await _factory.SeedUserAsync(
            tenantId, email, "route-1234", "Router", canViewAuditLog: true, canManageMailRouting: true);
        var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "route-1234"));
        return (client, tenantId, domain, email);
    }

    [Fact]
    public async Task A_claim_is_unverified_until_the_challenge_is_published()
    {
        var (client, _, domain, address) = await RouterAsync();
        using var _c = client;

        var created = await TestJson.Post(client, "/api/tenant/mail-domains", new { domain });
        var id = created.GetProperty("id").GetGuid();

        // Unverified, and told exactly what to publish where — a challenge the administrator cannot read is
        // not a challenge they can answer.
        Assert.False(created.GetProperty("verified").GetBoolean());
        var challengeName = created.GetProperty("challengeName").GetString()!;
        var challengeValue = created.GetProperty("challengeValue").GetString()!;
        Assert.Equal($"_simplarchive-challenge.{domain}", challengeName);
        Assert.StartsWith("simplarchive-domain-verification=", challengeValue, StringComparison.Ordinal);

        // Nothing published yet: the refusal names the record rather than merely failing, and carries it as a
        // Problem extension so a client can render it as a copyable field.
        var refused = await client.PostAsync($"/api/tenant/mail-domains/{id}/verify", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MAIL_DOMAIN_NOT_VERIFIED", problem.GetProperty("errorCode").GetString());
        Assert.Equal(challengeName, problem.GetProperty("challengeName").GetString());

        // Until it verifies, ingress must not accept it — that is the whole point of the flag.
        Assert.False(await ResolvesAsync(address));

        // Published, and checked again.
        _factory.Dns.Publish(challengeName, challengeValue);
        var verified = await TestJson.Post(client, $"/api/tenant/mail-domains/{id}/verify", new { });

        Assert.True(verified.GetProperty("verified").GetBoolean());

        // The challenge is withdrawn once it is answered: a verified domain still advertising a record to
        // publish invites someone to go looking for work that is done.
        Assert.Equal(JsonValueKind.Null, verified.GetProperty("challengeValue").ValueKind);
        Assert.DoesNotContain(
            verified.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "verify");

        // And now ingress resolves it.
        Assert.True(await ResolvesAsync(address));
    }

    [Fact]
    public async Task A_domain_another_tenant_holds_is_refused_without_naming_them()
    {
        var (first, _, domain, _) = await RouterAsync();
        using var _f = first;
        var created = await TestJson.Post(first, "/api/tenant/mail-domains", new { domain });
        _factory.Dns.Publish(created.GetProperty("challengeName").GetString()!, created.GetProperty("challengeValue").GetString()!);
        await TestJson.Post(first, $"/api/tenant/mail-domains/{created.GetProperty("id").GetGuid()}/verify", new { });

        var (second, _, _, _) = await RouterAsync();
        using var _s = second;

        var response = await second.PostAsJsonAsync("/api/tenant/mail-domains", new { domain });

        // A domain identifies exactly ONE tenant, so a second claim is refused rather than allowed to make
        // delivery ambiguous (ADR 0628's global unique index).
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MAIL_DOMAIN_ALREADY_CLAIMED", problem.GetProperty("errorCode").GetString());

        // And it does not say WHO holds it: that would answer "who else uses this product, for which domain"
        // to anyone able to type a guess.
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.DoesNotContain("T-", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Something_that_is_not_a_domain_is_refused_before_anything_is_stored()
    {
        var (client, tenantId, _, _) = await RouterAsync();
        using var _c = client;

        // The commonest mistake: the address instead of the domain part.
        var response = await client.PostAsJsonAsync("/api/tenant/mail-domains", new { domain = "admin@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_MAIL_DOMAIN",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>()
            .TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.TenantId == tenantId).ToListAsync());
    }

    [Fact]
    public async Task Without_the_routing_right_the_list_offers_nothing_and_the_writes_are_refused()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "plain-1234", "Plain");
        using var client = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "plain-1234"));

        // Readable — knowing which domains your own tenant receives on is not privileged — but the affordances
        // are simply absent rather than present and answering 403 (ADR 0543).
        var list = await TestJson.Get(client, "/api/tenant/mail-domains");
        Assert.False(list.GetProperty("canManage").GetBoolean());
        Assert.DoesNotContain(list.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "add");

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/tenant/mail-domains", new { domain = "example.com" })).StatusCode);
    }

    [Fact]
    public async Task Removing_a_domain_stops_ingress_accepting_it()
    {
        var (client, _, domain, address) = await RouterAsync();
        using var _c = client;

        var created = await TestJson.Post(client, "/api/tenant/mail-domains", new { domain });
        var id = created.GetProperty("id").GetGuid();
        _factory.Dns.Publish(created.GetProperty("challengeName").GetString()!, created.GetProperty("challengeValue").GetString()!);
        await TestJson.Post(client, $"/api/tenant/mail-domains/{id}/verify", new { });
        Assert.True(await ResolvesAsync(address));

        (await client.DeleteAsync($"/api/tenant/mail-domains/{id}")).EnsureSuccessStatusCode();

        // The effect of this one is only ever noticed by its absence, which is why it is audited — and why the
        // test asserts the DELIVERY consequence rather than that a row went away.
        Assert.False(await ResolvesAsync(address));

        var audit = await TestJson.Get(client, "/api/audit-events?action=MailDomain.Removed");
        Assert.Contains(
            audit.GetProperty("events").EnumerateArray(),
            e => e.GetProperty("action").GetString() == "MailDomain.Removed");
    }

    /// <summary>Whether ingress would accept this recipient — the question that actually matters.</summary>
    private async Task<bool> ResolvesAsync(string address)
    {
        using var scope = _factory.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Lmtp.LmtpDelivery>();
        return (await delivery.ResolveAsync(address, CancellationToken.None)).Count > 0;
    }

    [Fact]
    public async Task An_unverified_domain_is_refused_LOUDLY_rather_than_looking_like_an_unknown_one()
    {
        var (client, _, domain, address) = await RouterAsync();
        using var _c = client;

        await TestJson.Post(client, "/api/tenant/mail-domains", new { domain });

        // Both refusals are an empty result and a 550 to the sender, so from outside they are the same event.
        // They are not: one is a stranger's typo, the other is an administrator's unfinished task that nobody
        // is ever told about — a healthy install and a half-configured one otherwise log identically, which is
        // the failure mode ADR 0626 was written for.
        using var scope = _factory.Services.CreateScope();
        var log = new CapturingLogger<SimplArchive.Api.Lmtp.LmtpDelivery>();
        var delivery = new SimplArchive.Api.Lmtp.LmtpDelivery(
            scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>(),
            scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.ICurrentTenantAccessor>(),
            scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.IObjectStorageClient>(),
            scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.DocumentFinalizer>(),
            scope.ServiceProvider.GetRequiredService<SimplArchive.Api.Documents.PersonalMailboxProvisioner>(),
            log);

        Assert.Empty(await delivery.ResolveAsync(address, CancellationToken.None));

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("NOT VERIFIED", warning.Message, StringComparison.Ordinal);
        Assert.Contains(domain, warning.Message, StringComparison.OrdinalIgnoreCase);

        // An unknown domain must NOT produce the same warning — otherwise the signal is noise the first time
        // a spammer guesses a hostname.
        log.Entries.Clear();
        Assert.Empty(await delivery.ResolveAsync($"someone@{Guid.NewGuid():N}.invalid", CancellationToken.None));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>Collects what was logged, so a test can assert an administrator would actually be told.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
