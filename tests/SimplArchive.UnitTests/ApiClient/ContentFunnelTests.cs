using System.Text;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// The one content funnel (ADR 0830) as the shared library has it (ADR 0917): where an address goes, which client
// carries it, and what happens to an envelope. The desktop's suites cover it through the desktop; these cover the
// library alone, as the mobile app will use it.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class ContentFunnelTests : ApiClientSettingsTest
{
    private const string Installation = "https://archive.example.test";

    public ContentFunnelTests() => ApiClientSettings.ApiRootUrl = Installation;

    [Fact]
    public void A_relative_content_address_resolves_against_the_installation_and_an_absolute_one_is_kept()
    {
        // The token door is relative (ADR 0818). On Unix, a rooted path is itself an "absolute" file:// URI, which is
        // why the resolution tests the SCHEME and not only absoluteness.
        Assert.Equal(new Uri($"{Installation}/api/encrypted-content?t=abc"), ApiCore.ResolveContentUrl("/api/encrypted-content?t=abc"));
        Assert.Equal(new Uri("https://storage.example.test/bucket/key?X-Amz-Signature=s"), ApiCore.ResolveContentUrl("https://storage.example.test/bucket/key?X-Amz-Signature=s"));
        Assert.Equal(new Uri("http://storage.local:8333/b/k"), ApiCore.ResolveContentUrl("http://storage.local:8333/b/k"));
    }

    [Fact]
    public void Only_the_installation_itself_counts_as_own_whatever_the_path_or_case()
    {
        Assert.True(ApiCore.IsOwnInstallation(new Uri($"{Installation}/api/documents/1/versions/2/enveloped-content")));
        Assert.True(ApiCore.IsOwnInstallation(new Uri("HTTPS://ARCHIVE.example.test/anything")));
        Assert.False(ApiCore.IsOwnInstallation(new Uri("https://storage.example.test/api/documents")));
        Assert.False(ApiCore.IsOwnInstallation(new Uri("http://archive.example.test/api")));   // another scheme is another origin
    }

    [Fact]
    public async Task Plaintext_from_the_installation_arrives_untouched_and_the_opener_is_not_asked()
    {
        var handler = new FakeHandler(_ => FakeHandler.Bytes("hello"u8.ToArray(), "application/pdf"));
        ApiCore.Authenticated = new HttpClient(handler);
        var opener = new RecordingOpener();
        ApiClientSettings.EnvelopeOpener = opener;

        var (bytes, type, wasEnveloped) = await ApiCore.ReadContentAsync("/api/documents/1/versions/2/content");

        Assert.Equal("hello"u8.ToArray(), bytes);
        Assert.Equal("application/pdf", type);
        Assert.False(wasEnveloped);
        Assert.Empty(opener.Opened);
        Assert.Equal(new Uri($"{Installation}/api/documents/1/versions/2/content"), Assert.Single(handler.Requests).Uri);
    }

    [Fact]
    public async Task An_envelope_goes_to_the_hosts_opener_and_its_plaintext_comes_back()
    {
        var ciphertext = Encoding.ASCII.GetBytes("CMS-ENVELOPE");
        ApiCore.Authenticated = new HttpClient(new FakeHandler(_ => FakeHandler.Bytes(ciphertext, "application/pkcs7-mime")));
        var opener = new RecordingOpener();
        ApiClientSettings.EnvelopeOpener = opener;

        var (bytes, type, wasEnveloped) = await ApiCore.ReadContentAsync("/api/documents/1/versions/2/enveloped-content");

        Assert.True(wasEnveloped);
        Assert.Equal("plaintext"u8.ToArray(), bytes);
        Assert.Equal("text/plain", type);
        var (opened, servedType) = Assert.Single(opener.Opened);
        Assert.Equal(ciphertext, opened);
        Assert.Equal("application/pkcs7-mime", servedType);
    }

    [Fact]
    public async Task With_no_opener_configured_an_envelope_is_refused_by_name_never_passed_on_as_content()
    {
        ApiCore.Authenticated = new HttpClient(new FakeHandler(_ => FakeHandler.Bytes([1, 2, 3], "application/pkcs7-mime; smime-type=enveloped-data")));
        ApiClientSettings.EnvelopeOpener = DefaultOpener();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiCore.ReadContentAsync("/api/x/enveloped-content"));

        Assert.Contains("envelope", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_content_read_throws_rather_than_answering_an_empty_document()
    {
        ApiCore.Authenticated = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<HttpRequestException>(() => ApiCore.GetContentAsync("/api/documents/1/versions/2/content"));
    }

    [Theory]
    [InlineData("application/pkcs7-mime", true)]
    [InlineData("application/pkcs7-mime; smime-type=enveloped-data; name=smime.p7m", true)]
    [InlineData("APPLICATION/PKCS7-MIME", true)]
    [InlineData("application/pdf", false)]
    [InlineData("application/octet-stream", false)]
    public void Whether_a_served_type_is_an_envelope_is_answered_in_one_place(string contentType, bool expected) =>
        Assert.Equal(expected, Envelopes.IsEnvelope(contentType));

    /// <summary>The library's own default, reached without naming the internal type: a fresh settings value.</summary>
    private static IEnvelopeOpener DefaultOpener() =>
        (IEnvelopeOpener)typeof(ApiClientSettings).Assembly.GetType("SimplArchive.ApiClient.NoEnvelopeOpener")!
            .GetField("Instance")!.GetValue(null)!;

    private sealed class RecordingOpener : IEnvelopeOpener
    {
        public List<(byte[] Envelope, string ContentType)> Opened { get; } = [];

        public Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType)
        {
            Opened.Add((envelope, contentType));
            return Task.FromResult(("plaintext"u8.ToArray(), "text/plain"));
        }
    }
}
