using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop tree marks the OPEN folder — where you are (#686, ADR 0703). This class used to assert the
// opposite, which is what #696 shipped: the mark followed the SELECTED row, so it moved on a folder row and
// cleared on a document row, and the same gesture produced three different trees depending on what was under
// the cursor.
//
// The distinction is sharper on this client than on the web: here the tree's SelectedItem IS the navigation —
// setting it loads that folder's contents — so the mark cannot reuse selection, and it cannot follow
// SelectedTreeNode either, because drilling in from the list moves the open folder without moving the tree's
// selection. That gap is the whole reason the mark has its own state.
[Collection(UiCollection.Name)]
public class DesktopTreeMarkTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeMarkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Opening_a_folder_moves_the_mark_and_selecting_a_row_does_not()
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

        // Open the parent through the TREE. Opening is what moves the mark, so this is the first assertion.
        var repo = vm.Tree.First(n => n.Id == repoId);
        await repo.EnsureExpandedAsync();
        var parent = repo.Children.First(c => c.Id == parentId);
        vm.SelectedTreeNode = parent;
        await WaitForAsync(() => vm.Items.Any(i => i.Name == childName));
        await WaitForAsync(() => Marked(vm).Count == 1);

        Assert.Equal(parentName, Assert.Single(Marked(vm)).Name);

        // Select a row. Under #696 this moved the mark onto it; now the tree has nothing to say about a
        // selection, because selecting is not moving.
        vm.SelectedItem = vm.Items.First(i => i.Name == childName);
        await WaitForAsync(() => vm.DetailTitle == childName);

        Assert.Equal(parentName, Assert.Single(Marked(vm)).Name);

        // ...and the detail pane DOES describe the selected row, so the ring standing still is not the pane
        // failing to follow. Both halves, or "nothing moved" would be satisfied by nothing working.
        Assert.Equal(childName, vm.DetailTitle);

        // Now OPEN that child. The mark moves, to a node that is not the tree's selected one — which is exactly
        // the case a mark reusing SelectedTreeNode could not express.
        await vm.OpenCommand.ExecuteAsync(null);
        await WaitForAsync(() => Marked(vm).Count == 1 && Marked(vm)[0].Name == childName);

        Assert.Equal(childName, Assert.Single(Marked(vm)).Name);
        Assert.Equal(parentId, vm.SelectedTreeNode!.Id);
        Assert.DoesNotContain(vm.Items, i => i.Name == siblingName); // the listing moved with it
    }

    // The mark's meaning must NOT change with the row type — that was the second half of the #696 defect: a
    // folder row moved it and a document row cleared it.
    //
    // The old version of this test guarded its document case with `if (…FirstOrDefault(i => !i.IsFolder) is { }
    // document)`, against a folder whose children are all folders — so the assertion never ran and the test
    // passed by finding nothing. Here the document is asserted to exist first, so an empty listing fails
    // instead of quietly agreeing.
    [Fact]
    public async Task Selecting_a_document_leaves_the_mark_on_the_folder_you_are_standing_in()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        // A folder holding a REAL document, made here rather than looked for: the version this test needs is a
        // confirmed one, and a listing that happens to contain a document is a fixture assumption that can stop
        // being true without anyone noticing.
        var folderName = $"dm{Guid.NewGuid():N}"[..8];
        var folderId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = folderName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var docName = $"dc{Guid.NewGuid():N}"[..8];
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{folderId}/children", new { name = docName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("hello")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(new SimplArchiveApiClient(token), SelfHostedAppFixture.AdminEmail);

        // Reached through the TREE, which is how a user gets here. Marking is best-effort within the LOADED
        // tree (ADR 0703): a folder whose ancestors have never been expanded is not in the tree to be marked,
        // so opening one by address alone legitimately marks nothing — that is the documented answer, not a
        // gap to assert against.
        var repoNode = vm.Tree.First(n => n.Id == repoId);
        await repoNode.EnsureExpandedAsync();
        vm.SelectedTreeNode = repoNode.Children.First(c => c.Id == folderId);
        await WaitForAsync(() => vm.Items.Any(i => i.Name == docName));
        await WaitForAsync(() => Marked(vm).Count == 1);

        Assert.Equal(folderName, Assert.Single(Marked(vm)).Name);

        vm.SelectedItem = vm.Items.First(i => i.Name == docName);
        await WaitForAsync(() => vm.DetailTitle == docName);

        // Under #696 this CLEARED the mark, because a document is not in the folders-only tree. The tree is not
        // answering "what is selected" any more, so a document row is not its business at all.
        Assert.Equal(folderName, Assert.Single(Marked(vm)).Name);
    }

    // The other half of ADR 0703: with nothing selected, the pane describes the folder being stood in. A
    // repository root has no parent to be listed in, so before this its own mask and index fields were not
    // reachable at all.
    [Fact]
    public async Task With_nothing_selected_the_pane_describes_the_open_folder()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl)), SelfHostedAppFixture.AdminEmail);

        var repo = vm.Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });
        vm.SelectedTreeNode = repo;
        await WaitForAsync(() => vm.Items.Count > 0);
        await WaitForAsync(() => vm.DetailTitle == repo.Name);

        Assert.Null(vm.SelectedItem);
        Assert.Equal(repo.Name, vm.DetailTitle);
    }

    private static List<TreeNodeViewModel> Marked(MainWindowViewModel vm) =>
        OpenFolderMark.Flatten(vm.Tree).Where(n => n.IsMarked).ToList();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
