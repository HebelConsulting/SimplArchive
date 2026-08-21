using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop tree marks the SELECTED folder (#696), completing #686 on this client.
//
// The distinction is the same one the web needed, and it is sharper here: on this client the tree's
// SelectedItem IS the navigation — setting it loads that folder's contents. So marking a node cannot reuse
// selection, and a test that only checked "the tree knows about the folder" would pass against a version that
// navigated, which is the outcome that must be ruled out.
[Collection(UiCollection.Name)]
public class DesktopTreeMarkTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeMarkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_a_folder_marks_it_in_the_tree_without_opening_it()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var parentName = $"pm{Guid.NewGuid():N}"[..8];
        var parentId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = parentName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var childName = $"cm{Guid.NewGuid():N}"[..8];
        (await http.PostAsJsonAsync($"/api/documents/{parentId}/children", new { name = childName, folderMask = "folder" })).EnsureSuccessStatusCode();
        var siblingName = $"sb{Guid.NewGuid():N}"[..8];
        (await http.PostAsJsonAsync($"/api/documents/{parentId}/children", new { name = siblingName, folderMask = "folder" })).EnsureSuccessStatusCode();

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(new SimplArchiveApiClient(token), SelfHostedAppFixture.AdminEmail);

        // Open the parent through the TREE, which is what makes it SelectedTreeNode — the state the mark is
        // looked up relative to.
        var repo = vm.Tree.First(n => n.Id == repoId);
        await repo.EnsureExpandedAsync();
        var parent = repo.Children.First(c => c.Id == parentId);
        vm.SelectedTreeNode = parent;
        await WaitForAsync(() => vm.Items.Any(i => i.Name == childName));

        var listedChild = vm.Items.First(i => i.Name == childName);
        vm.SelectedItem = listedChild;
        await WaitForAsync(() => parent.Children.Any(c => c.Name == childName && c.IsMarked));

        // Marked — exactly one node, and it is the child.
        var marked = AllNodes(vm.Tree).Where(n => n.IsMarked).ToList();
        Assert.Single(marked);
        Assert.Equal(childName, marked[0].Name);

        // NOT opened: the listing still shows the parent's contents, so the sibling is still there. This is the
        // half that would be lost by reusing SelectedTreeNode, and it is why the mark needed its own state.
        Assert.Equal(parentId, vm.SelectedTreeNode!.Id);
        Assert.Contains(vm.Items, i => i.Name == siblingName);

        // ...and selecting the sibling MOVES the mark rather than adding a second claim about the subject.
        vm.SelectedItem = vm.Items.First(i => i.Name == siblingName);
        await WaitForAsync(() => parent.Children.Any(c => c.Name == siblingName && c.IsMarked));
        var moved = AllNodes(vm.Tree).Where(n => n.IsMarked).ToList();
        Assert.Single(moved);
        Assert.Equal(siblingName, moved[0].Name);
    }

    // A DOCUMENT is not in the folders-only tree, so nothing there is the subject — the mark clears rather than
    // being left on whatever folder was last selected, which would be a claim about the wrong object.
    [Fact]
    public async Task Selecting_a_document_clears_the_mark()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl)), SelfHostedAppFixture.AdminEmail);

        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        vm.SelectedTreeNode = personal;
        await WaitForAsync(() => vm.Items.Count > 0);

        if (vm.Items.FirstOrDefault(i => i.IsFolder) is { } folder)
        {
            vm.SelectedItem = folder;
            await WaitForAsync(() => AllNodes(vm.Tree).Any(n => n.IsMarked));
        }

        if (vm.Items.FirstOrDefault(i => !i.IsFolder) is { } document)
        {
            vm.SelectedItem = document;
            await WaitForAsync(() => !AllNodes(vm.Tree).Any(n => n.IsMarked));
            Assert.DoesNotContain(AllNodes(vm.Tree), n => n.IsMarked);
        }
    }

    private static IEnumerable<TreeNodeViewModel> AllNodes(IEnumerable<TreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Children)) yield return child;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
