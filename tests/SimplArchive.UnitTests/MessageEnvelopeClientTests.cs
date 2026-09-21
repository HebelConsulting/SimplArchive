using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Encryption;

namespace SimplArchive.UnitTests;

// The core's client for the encryption service's download leg (SimplArchiveEncryption ADR 0007). What these
// pin is the CONTRACT the IMAP funnel builds on: null always means "serve what you built", and the three
// ways of reaching null — unconfigured, no certificate, service down — must all resolve there, because the
// funnel has exactly one fallback and a throw anywhere in this class would take a FETCH down with it.
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

    private static MessageEnvelopeClient Client(string? serviceUrl, HttpMessageHandler handler) =>
        new(new StubFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Encryption:ServiceUrl"] = serviceUrl }).Build(),
            NullLogger<MessageEnvelopeClient>.Instance);

    [Fact]
    public async Task Unconfigured_answers_null_without_any_call()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(null, handler);

        Assert.Null(await client.TryEnvelopeAsync("a@b.test", [1, 2], CancellationToken.None));
        Assert.False(client.Enabled);
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

        Assert.Equal([9, 9, 9], await client.TryEnvelopeAsync("a@b.test", [1], CancellationToken.None));
        Assert.True(client.Enabled);
    }

    [Fact]
    public async Task No_certificate_is_null_not_an_exception()
    {
        var client = Client("http://encryption:8080",
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Assert.Null(await client.TryEnvelopeAsync("a@b.test", [1], CancellationToken.None));
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
        Assert.Null(await erroring.TryEnvelopeAsync("a@b.test", [1], CancellationToken.None));

        var throwing = Client("http://encryption:8080",
            new StubHandler(_ => throw new HttpRequestException("connection refused")));
        Assert.Null(await throwing.TryEnvelopeAsync("a@b.test", [1], CancellationToken.None));
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

        await Client("http://encryption:8080/", handler).TryEnvelopeAsync("anna+test@ex.test", [1], CancellationToken.None);

        // The trailing slash on the configured URL must not double, and the address must be escaped — a raw
        // '+' in a route decodes to a space on the service side and misses the registry silently.
        Assert.Equal("http://encryption:8080/api/users/anna%2Btest%40ex.test/enveloped", requested);
    }
}
