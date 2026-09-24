using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Encryption;

namespace SimplArchive.UnitTests;

// The core's client for the encryption service's download leg (SimplArchiveEncryption ADR 0007). What these
// pin is the CONTRACT the IMAP funnel builds on: null always means "serve what you built", and the four
// ways of reaching null — unconfigured, tenant not listed (ADR 0813), no certificate, service down — must
// all resolve there, because the funnel has exactly one fallback and a throw anywhere in this class would
// take a FETCH down with it.
public class MessageEnvelopeClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static MessageEnvelopeClient Client(
        string? serviceUrl, HttpMessageHandler handler, params string[] tenants)
    {
        // ADR 0825: a mode per tenant. No named tenants means the installation default covers everything,
        // which is the successor to the old "empty list means every tenant".
        var settings = new Dictionary<string, string?> { ["Encryption:ServiceUrl"] = serviceUrl };
        if (tenants.Length == 0)
        {
            settings["Encryption:DefaultMode"] = "Storage";
        }

        foreach (var tenant in tenants)
        {
            settings[$"Encryption:Modes:{tenant}"] = "Storage";
        }

        return new(new StubFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<MessageEnvelopeClient>.Instance);
    }

    [Fact]
    public async Task Unconfigured_answers_null_without_any_call()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(null, handler);

        Assert.Null(await client.TryEnvelopeAsync("Acme", "a@b.test", [1, 2], CancellationToken.None));
        Assert.False(client.EnabledFor("Acme"));
        // Zero calls is the point: an installation without the service must not even resolve its hostname.
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_success_returns_the_service_bytes()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 9, 9]),
        });
        var client = Client("http://encryption:8080", handler);

        Assert.Equal([9, 9, 9], await client.TryEnvelopeAsync("Acme", "a@b.test", [1], CancellationToken.None));
        Assert.True(client.EnabledFor("Acme"));
    }

    [Fact]
    public async Task An_unlisted_tenant_answers_null_without_any_call()
    {
        // The per-tenant half of the switch (ADR 0813, now a mode map — ADR 0825): a tenant with no mode
        // behaves exactly like an unconfigured installation — no call, no hostname resolution, plaintext.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 9, 9]),
        });
        var client = Client("http://encryption:8080", handler, "Crypto");

        Assert.Null(await client.TryEnvelopeAsync("Acme", "a@b.test", [1], CancellationToken.None));
        Assert.False(client.EnabledFor("Acme"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_listed_tenant_envelopes_and_the_match_ignores_case()
    {
        // Case-insensitive on purpose: the map is operator-typed configuration, and "crypto" failing to
        // match "Crypto" would fail silently into plaintext — the wrong direction to fail quietly in.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 9, 9]),
        });
        var client = Client("http://encryption:8080", handler, "crypto");

        Assert.True(client.EnabledFor("Crypto"));
        Assert.Equal([9, 9, 9], await client.TryEnvelopeAsync("Crypto", "a@b.test", [1], CancellationToken.None));
    }

    [Fact]
    public void An_installation_default_covers_a_tenant_with_no_entry_of_its_own()
    {
        // Succeeds the old "a whitespace-only list means every tenant" case. That existed because the compose
        // passthrough `Encryption__Tenants__0: ${ENCRYPTION_TENANT:-}` yielded one EMPTY element when the
        // variable was unset, which had to read as "no list" rather than "a tenant named nothing" — a
        // fragility that came from encoding a policy default in the absence of a value. Under ADR 0825 the
        // installation-wide answer is its own setting, so there is no empty element to interpret.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.True(Client("http://encryption:8080", handler).EnabledFor("Acme"));
    }

    [Fact]
    public async Task No_certificate_is_null_not_an_exception()
    {
        var client = Client("http://encryption:8080",
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await client.TryEnvelopeAsync("Acme", "a@b.test", [1], CancellationToken.None));
    }

    [Fact]
    public async Task A_broken_service_fails_open_in_milestone_one()
    {
        // 500s and connection failures both land here. Milestone 1 serves plaintext because the blobs ARE
        // plaintext at rest — the envelope adds transport protection, it does not yet guard a secret the
        // caller could not otherwise have. Milestone 2 makes fail-closed structural: undecryptable blobs
        // cannot be served plaintext by any code path.
        var erroring = Client("http://encryption:8080",
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Null(await erroring.TryEnvelopeAsync("Acme", "a@b.test", [1], CancellationToken.None));

        var throwing = Client("http://encryption:8080",
            new StubHandler(_ => throw new HttpRequestException("connection refused")));
        Assert.Null(await throwing.TryEnvelopeAsync("Acme", "a@b.test", [1], CancellationToken.None));
    }

    [Fact]
    public async Task The_recipient_email_is_escaped_into_the_route()
    {
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await Client("http://encryption:8080/", handler).TryEnvelopeAsync("Acme", "anna+test@ex.test", [1], CancellationToken.None);

        // The trailing slash on the configured URL must not double, and the address must be escaped — a raw
        // '+' in a route decodes to a space on the service side and misses the registry silently.
        Assert.Equal("http://encryption:8080/api/users/anna%2Btest%40ex.test/enveloped", requested);
    }

    /// <summary>
    /// The certificate fetch (#1334) hits the registry GET with the same escaping rules, returns the PEM
    /// on 200, and answers null on 404 and on an unlisted tenant — every miss meaning "certificate-less",
    /// the notification dispatcher's plaintext contract.
    /// </summary>
    [Fact]
    public async Task The_certificate_fetch_uses_the_registry_route_and_misses_answer_null()
    {
        string? requested = null;
        var handler = new StubHandler(request =>
        {
            requested = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("-----BEGIN CERTIFICATE-----\nX\n-----END CERTIFICATE-----"),
            };
        });

        var pem = await Client("http://encryption:8080/", handler)
            .TryGetCertificatePemAsync("Acme", "anna+test@ex.test", CancellationToken.None);

        Assert.Equal("http://encryption:8080/api/users/anna%2Btest%40ex.test/certificate", requested);
        Assert.Contains("BEGIN CERTIFICATE", pem, StringComparison.Ordinal);

        var missing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await Client("http://encryption:8080", missing)
            .TryGetCertificatePemAsync("Acme", "anna@ex.test", CancellationToken.None));

        // Unlisted tenant: no request at all — the gate answers before the network does.
        string? unlistedRequest = null;
        var unlisted = new StubHandler(request => { unlistedRequest = request.RequestUri!.ToString(); return new HttpResponseMessage(HttpStatusCode.OK); });
        Assert.Null(await Client("http://encryption:8080", unlisted, "Other")
            .TryGetCertificatePemAsync("Acme", "anna@ex.test", CancellationToken.None));
        Assert.Null(unlistedRequest);
    }
}
