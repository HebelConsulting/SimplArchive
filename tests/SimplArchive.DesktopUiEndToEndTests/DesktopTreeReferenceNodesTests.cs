using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// A folder that advertises no `references` rel has no shortcuts — it does not have a broken tree (#735).
//
// The rel is REQUIRED of nothing: a missing rel means "not available to you, here, now" (ADR 0543), which for
// shortcuts reads as "none are filed here". It used to throw instead, on the expansion path, which has no
// handler above it — so one listing that omitted one rel killed the whole client.
//
// Asserted here rather than end-to-end because the server now advertises the rel everywhere the tree can reach,
// so no fixture can produce the shape this guards. The DocumentsClient is deliberately null: the point is that
// nothing is fetched at all.
public class DesktopTreeReferenceNodesTests
{
    [Fact]
    public async Task A_node_without_the_rel_contributes_no_shortcuts_and_asks_no_one()
    {
        var node = new TreeNodeViewModel(
            Guid.NewGuid(), "Carol", hasSubfolders: true, loadChildren: null,
            links: new Dictionary<string, string> { ["children"] = "/api/documents/x/children" });

        Assert.Empty(await TreeReferenceNodes.ForAsync(node, references: null!, expand: _ => throw new Xunit.Sdk.XunitException("must not expand")));
    }

    [Fact]
    public async Task A_node_with_no_links_at_all_is_the_same_answer()
    {
        // The synthetic rows — Administration, the personal groupings — carry no links whatsoever.
        var node = new TreeNodeViewModel(Guid.Empty, "Administration", hasSubfolders: true, loadChildren: null);

        Assert.Empty(await TreeReferenceNodes.ForAsync(node, references: null!, expand: _ => throw new Xunit.Sdk.XunitException("must not expand")));
    }
}
