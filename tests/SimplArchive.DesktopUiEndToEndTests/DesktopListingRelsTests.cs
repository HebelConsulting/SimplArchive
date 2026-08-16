using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// A row the client holds must carry the addresses it will follow (ADR 0543, issue #416).
//
// This exists because the parser silently stopped populating them and nothing noticed. `ParseNode` gained a
// `Links` field but never had the line that fills it, so every Node.Links was null and every Href() threw — and
// that shipped in 2aeaae0. Nothing caught it at build time (the field is optional and defaults to null) and the
// tests that DO exercise Href were reported green by a run that could not have been executing that code.
//
// So this asserts the contract end to end rather than the plumbing: fetch a real listing from a real server, and
// require that each row advertises what the client actually follows. It fails if the API stops advertising a rel,
// if the parser stops reading them, or if the two disagree about the shape — the three ways this can break.
[Collection(UiCollection.Name)]
public class DesktopListingRelsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopListingRelsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Rows_from_a_listing_carry_the_rels_the_client_follows()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // A repository row — the entry point to the tree, which had only `self` until #416.
        var repo = (await api.Documents.GetRepositoriesAsync())[0];
        Assert.NotNull(repo.Links);
        Assert.Equal($"api/documents/{repo.Id}/children", repo.Href("children"));

        // A child row — every node the tree and the contents list are built from.
        var children = await api.Documents.GetChildrenAsync(repo.Href("children"));
        Assert.NotEmpty(children);
        foreach (var rel in new[] { "children", "versions", "mask", "index-data", "chat" })
        {
            Assert.All(children, c => Assert.False(string.IsNullOrEmpty(c.Href(rel))));
        }

        // And the negative: a rel the listing deliberately does NOT advertise must throw rather than resolve to a
        // composed guess. Conditional affordances (checkout, external-links, acl-inheritance) depend on per-row
        // rights a listing does not compute, so they require the resource itself.
        var ex = Assert.Throws<InvalidOperationException>(() => children[0].Href("checkout"));
        Assert.Contains("not advertised", ex.Message);
    }
}
