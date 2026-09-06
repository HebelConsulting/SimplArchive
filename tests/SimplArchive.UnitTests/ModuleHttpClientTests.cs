using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// The guarded per-module egress client (ABI 0.6): a module reaches ONLY the hosts it declared in
// OutboundHosts, and only http/https — the allowlist gate that keeps a module's network reach as enumerable
// and contained as its archive reach. The SSRF address-pinning half is proven by the OutboundAddressPolicy
// tests; here it is the hostname allowlist and the response mapping.
public class ModuleHttpClientTests
{
    private sealed class FakeModule(string id, IReadOnlyList<string> hosts) : IIndustryModule
    {
        public string ModuleId => id;
        public string DisplayName => id;
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [];
        public IReadOnlyList<string> OutboundHosts { get; } = hosts;
        public void ConfigureServices(IServiceCollection services) { }
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private static ModuleHttpClient ClientFor(string moduleId, IReadOnlyList<string> hosts, HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new StubHandler(new HttpResponseMessage(HttpStatusCode.OK))),
            new ModuleIdentityAccessor { ModuleId = moduleId },
            [new ModuleLoader.LoadedModule(new FakeModule(moduleId, hosts), "path")]);

    [Fact]
    public async Task A_host_the_module_did_not_declare_is_refused_before_any_request_leaves()
    {
        var client = ClientFor("fs", ["aviationweather.gov"]);

        var refused = await Assert.ThrowsAsync<ModuleOutboundRefusedException>(
            () => client.GetAsync("https://evil.example/steal"));
        Assert.Contains("evil.example", refused.Message);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://aviationweather.gov/x")]
    [InlineData("not-a-url")]
    public async Task A_non_http_or_malformed_url_is_refused(string url)
    {
        var client = ClientFor("fs", ["aviationweather.gov"]);
        await Assert.ThrowsAsync<ModuleOutboundRefusedException>(() => client.GetAsync(url));
    }

    [Fact]
    public async Task Outside_a_module_scope_there_is_no_allowlist_to_check_against()
    {
        // No acting module — a refusal, not a silent send with nobody's allowlist.
        var client = new ModuleHttpClient(new HttpClient(new StubHandler(new HttpResponseMessage())),
            new ModuleIdentityAccessor(), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://aviationweather.gov/"));
    }

    [Fact]
    public async Task A_declared_host_passes_and_the_response_maps_to_the_dto()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("METAR LSZH")),
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var client = ClientFor("fs", ["aviationweather.gov"], new StubHandler(response));

        var result = await client.GetAsync("https://aviationweather.gov/api/data/metar?ids=LSZH");

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("\"v1\"", result.ETag);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal("METAR LSZH", Encoding.UTF8.GetString(result.Content));
    }
}
