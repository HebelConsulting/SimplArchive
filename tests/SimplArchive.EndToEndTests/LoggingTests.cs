namespace SimplArchive.EndToEndTests;

// End-to-end for the logging pipeline (ADR "Enterprise-grade structured logging with Serilog"): every response
// carries an X-Correlation-ID, a caller-supplied one is echoed back, and — implicitly — the app boots and serves
// requests with Serilog installed as the host logger (WebApplicationFactory<Program> runs the real Program).
[Collection(E2ECollection.Name)]
public class LoggingTests
{
    private readonly E2EApiFactory _factory;

    public LoggingTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Response_carries_a_correlation_id_and_echoes_a_supplied_one()
    {
        using var client = _factory.CreateClient();

        // A generated id when the caller supplies none (GET /api is the anonymous discovery document).
        var generated = await client.GetAsync("/api");
        Assert.True(generated.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.False(string.IsNullOrWhiteSpace(values!.Single()));

        // A caller-supplied id is echoed back unchanged.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        request.Headers.Add("X-Correlation-ID", "e2e-corr-42");
        var echoed = await client.SendAsync(request);
        Assert.Equal("e2e-corr-42", echoed.Headers.GetValues("X-Correlation-ID").Single());
    }
}
