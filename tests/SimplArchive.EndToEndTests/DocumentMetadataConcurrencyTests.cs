using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// DocumentMetadataController had the whole concurrency apparatus as DEAD CODE (#1083): SetETag defined and
// called nowhere, TryParseETag defined and called nowhere, and not one occurrence of "If-Match" in the file —
// across mutations of `Document`, which IS concurrency-tracked. A tracked entity whose endpoints never check
// the token is no better than an untracked one.
//
// The subtle one is PUT index-data. It writes FieldValue CHILD rows, and EF checks a concurrency token only on
// rows it is actually UPDATING — so while the Document row stayed untouched, the parent's token never fired and
// the most collision-prone edit in the app was unguarded. It now marks the document's token modified, which is
// what brings the row into the UPDATE and makes the caller's precondition mean something.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class DocumentMetadataConcurrencyTests
{
    private readonly E2EApiFactory _factory;

    public DocumentMetadataConcurrencyTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Api, Guid DocumentId)> FolderAsync()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repo = await TestJson.Post(api, "/api/repositories", new { name = $"meta-{Guid.NewGuid():N}"[..12] });
        return (api, repo.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_index_data_read_now_carries_an_etag_at_all()
    {
        // It emitted none: SetETag existed and nothing called it, so a client had nothing to send back.
        var (api, documentId) = await FolderAsync();
        using var _a = api;

        var read = await api.GetAsync($"/api/documents/{documentId}/index-data");
        read.EnsureSuccessStatusCode();

        Assert.False(string.IsNullOrWhiteSpace(read.Headers.ETag?.Tag), "the index-data read emitted no ETag");
    }

    [Fact]
    public async Task A_stale_token_is_refused_on_the_contents_sort_order()
    {
        var (api, documentId) = await FolderAsync();
        using var _a = api;

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/contents-sort-order")
        {
            Content = JsonContent.Create(new { sortOrder = 0 }), // Name; the enum goes over the wire as its number
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{Guid.NewGuid()}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await api.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Index_data_honours_the_precondition_even_though_it_writes_child_rows()
    {
        // THE point of this change. Before it, a stale token here was simply ignored: the Document row was not
        // being updated, so EF had nothing to compare and the write went through as if nobody else had edited.
        var (api, documentId) = await FolderAsync();
        using var _a = api;

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/index-data")
        {
            Content = JsonContent.Create(new { fields = Array.Empty<object>() }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{Guid.NewGuid()}\"");

        var response = await api.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Contains("ETAG_MISMATCH", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fresh_token_succeeds_and_then_that_same_token_is_stale()
    {
        // The round trip: read the tag, write with it, and find the SAME tag refused afterwards — which is what
        // proves index-data now MOVES the document's version rather than leaving it where it was.
        var (api, documentId) = await FolderAsync();
        using var _a = api;

        var read = await api.GetAsync($"/api/documents/{documentId}/index-data");
        var etag = read.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        using var first = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/index-data")
        {
            Content = JsonContent.Create(new { fields = Array.Empty<object>() }),
        };
        first.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(first)).StatusCode);

        using var second = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/index-data")
        {
            Content = JsonContent.Create(new { fields = Array.Empty<object>() }),
        };
        second.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await api.SendAsync(second)).StatusCode);
    }

    [Fact]
    public async Task A_write_without_if_match_is_still_accepted()
    {
        // The staged contract: the web client calls these addresses with a plain PutAsJsonAsync and sends no
        // header at all, so demanding one would answer 428 to every save today. Pinned so requiring it later is
        // a deliberate edit.
        var (api, documentId) = await FolderAsync();
        using var _a = api;

        var response = await api.PutAsJsonAsync($"/api/documents/{documentId}/index-data", new { fields = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
