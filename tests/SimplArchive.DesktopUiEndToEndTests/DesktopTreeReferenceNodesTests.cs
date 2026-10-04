using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.DesktopClient.Services;

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
[Collection(UiCollection.Name)]
public class DesktopTreeReferenceNodesTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_node_without_the_rel_contributes_no_shortcuts_and_asks_no_one()
    {
        var node = new TreeNodeViewModel(
            Guid.NewGuid(), "Carol", hasSubfolders: true, loadChildren: null,
            links: LinkMap.FromHrefs(new Dictionary<string, string> { ["children"] = "/api/documents/x/children" }));

        Assert.Empty(await TreeReferenceNodes.ForAsync(node, references: null!, expand: _ => throw new Xunit.Sdk.XunitException("must not expand")));
    }

    [Fact]
    public async Task A_node_with_no_links_at_all_is_the_same_answer()
    {
        // The synthetic rows — Administration, the personal groupings — carry no links whatsoever.
        var node = new TreeNodeViewModel(Guid.Empty, "Administration", hasSubfolders: true, loadChildren: null);

        Assert.Empty(await TreeReferenceNodes.ForAsync(node, references: null!, expand: _ => throw new Xunit.Sdk.XunitException("must not expand")));
    }

    // A folder SHORTCUT in the tree must be openable: the folder loader needs the target's `children` AND
    // `references` and has no id fallback (ADR 0543). The node used to be built from the target's id alone, so
    // selecting it threw "No advertised address for folder …" — reported on the flight school's Aerodrome
    // shortcuts in My Documents, and true of every folder shortcut in the desktop tree.
    [Fact]
    public async Task A_folder_shortcut_in_the_tree_carries_its_target_addresses()
    {
        SimplArchive.DesktopClient.DesktopClientOptions.ApiBaseUrl = app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var holderName = $"ref-holder-{Guid.NewGuid():N}";
        var targetName = $"ref-target-{Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Href("children"), holderName);
        await api.Documents.CreateFolderAsync(repo.Href("children"), targetName);
        var rows = await api.Documents.GetChildrenAsync(repo.Href("children"));
        var holder = rows.Single(n => n.Name == holderName);
        var target = rows.Single(n => n.Name == targetName);
        await api.References.CreateReferenceAsync(holder.Href("references"), target.Id);

        var holderNode = new TreeNodeViewModel(holder.Id, holder.Name, hasSubfolders: true, loadChildren: null, links: holder.Links);
        var shortcuts = await TreeReferenceNodes.ForAsync(
            holderNode, api.References, expand: _ => Task.FromResult(Enumerable.Empty<TreeNodeViewModel>()));

        var shortcut = Assert.Single(shortcuts);
        Assert.True(shortcut.IsReference);
        Assert.Equal(target.Id, shortcut.Id);
        Assert.NotNull(shortcut.Href("children"));    // what the folder loader follows to list it
        Assert.NotNull(shortcut.Href("references"));  // and to list ITS shortcuts
    }
}
