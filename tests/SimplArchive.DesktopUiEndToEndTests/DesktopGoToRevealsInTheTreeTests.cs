using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// "Go to …" on a reference takes the TREE along, not just the list pane (#1264).
//
// WHAT WAS WRONG. `GoToReferenceAsync` followed the row's `go-to` rel into `OpenFolderAsync`, which loads the
// contents pane and — by its own comment — leaves "the tree isn't re-synced". So the list moved to the target's
// real home while the tree still pointed at the folder holding the shortcut, and the tree's one job is to
// answer "where am I" (ADR 0703).
//
// The reason it went unnoticed is that the SIBLING path was right: a search hit reveals, because
// `OpenSearchResultAsync` calls `RevealDocumentInTreeAsync`. One act — take me to where this really lives — had
// two behaviours depending on which surface it started from.
//
// WHY THE ASSERTION IS ON THE TREE. Asserting the list moved is what a test written from the symptom would do,
// and it passed the whole time. The defect is only visible in `SelectedTreeNode`.
[Collection(UiCollection.Name)]
public class DesktopGoToRevealsInTheTreeTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopGoToRevealsInTheTreeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Go_to_on_a_reference_selects_the_targets_real_parent_in_the_tree()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // A document NESTED one level down in branch A, and a shortcut to it in a separate branch B. Nested on
        // purpose: if the target sat at the repository root, its parent would already be a top-level tree node
        // and the test could pass without any ancestor expansion happening at all.
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var tag = Guid.NewGuid().ToString("N")[..8];
        await api.Documents.CreateFolderAsync(repo.Href("children"), $"gt-a-{tag}");
        await api.Documents.CreateFolderAsync(repo.Href("children"), $"gt-b-{tag}");
        var branchA = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == $"gt-a-{tag}");
        var branchB = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == $"gt-b-{tag}");

        await api.Documents.CreateFolderAsync(branchA.Href("children"), $"gt-home-{tag}");
        var home = (await api.Documents.GetChildrenAsync(branchA.Href("children"))).Single(n => n.Name == $"gt-home-{tag}");

        var fileName = $"gt-doc-{tag}.txt";
        await api.Documents.UploadFileAsync(home.Href("children"), fileName, Encoding.UTF8.GetBytes("body"));
        var document = (await api.Documents.GetChildrenAsync(home.Href("children")))
            .Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));

        await api.References.CreateReferenceAsync(branchB.Href("references"), document.Id);

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Stand in branch B — where the shortcut lives — exactly as a user would before clicking Go to.
        var repositoryNode = vm.Tree.First(n => n.Id == repo.Id);
        await repositoryNode.EnsureExpandedAsync();
        var branchBNode = repositoryNode.Children.Single(n => n.Id == branchB.Id);
        vm.SelectedTreeNode = branchBNode;
        await WaitForAsync(() => vm.Items.Any(i => i.IsReference && i.Id == document.Id), seconds: 20);

        var shortcut = vm.Items.Single(i => i.IsReference && i.Id == document.Id);
        Assert.Equal(branchBNode.Id, vm.SelectedTreeNode!.Id);   // precondition: the tree is on B

        await vm.GoToReferenceAsync(shortcut);
        await WaitForAsync(() => vm.SelectedTreeNode is { } n && n.Id == home.Id, seconds: 20);

        // THE ASSERTION THE BUG IS ABOUT: the tree followed. Before the fix it stayed on branch B while the list
        // showed the target's home folder — two panes describing different places.
        Assert.Equal(home.Id, vm.SelectedTreeNode!.Id);

        // And the list went too, with the target selected — the half that already worked and must keep working.
        Assert.Contains(vm.Items, i => i.Id == document.Id && !i.IsReference);
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {seconds}s waiting for the condition.");
    }
}
