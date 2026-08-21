using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The detail pane describes a selected FOLDER (#686), which on the desktop it did not.
//
// The failure was not an empty pane — it was worse. LoadDetailAsync ran only for non-folders, so selecting a
// folder left the PREVIOUS document's name, fields and preview on screen while the list showed a folder. That
// is the stale-subject condition ADR 0559 exists to prevent, and it is invisible in the way that matters: the
// pane looks populated and correct, and describes the wrong object.
//
// So the load-bearing step is the SECOND selection. A test that selected a folder from a clean start would
// have passed against the old code too, because there was no previous subject to inherit.
[Collection(UiCollection.Name)]
public class DesktopFolderDetailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopFolderDetailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_folder_selected_after_a_document_replaces_the_documents_details()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);

        // Seeded over HTTP rather than through the view-model: creating from the VM kicks off its own tree and
        // list refresh, which clears the selection out from under the assertions — the first version of this
        // test raced it and watched DetailTitle get set and then blanked.
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var parent = $"pd{Guid.NewGuid():N}"[..8];
        var parentId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = parent })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // One folder and one real document, side by side in the same listing.
        var childFolder = $"cf{Guid.NewGuid():N}"[..8];
        (await http.PostAsJsonAsync($"/api/documents/{parentId}/children", new { name = childFolder, folderMask = "folder" })).EnsureSuccessStatusCode();

        var docName = $"dc{Guid.NewGuid():N}"[..8];
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{parentId}/children", new { name = docName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("hello")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await vm.OpenFolderAsync($"/api/documents/{parentId}");

        var docRow = vm.Items.First(i => i.Name == docName);
        var folderRow = vm.Items.First(i => i.Name == childFolder);

        // The previous subject: a real document, fully described.
        vm.SelectedItem = docRow;
        await WaitForAsync(() => vm.DetailTitle == docName && vm.SysCurrentVersion != "");
        Assert.Equal(docName, vm.DetailTitle);
        Assert.NotEqual("", vm.SysCurrentVersion);

        // Now the folder. Before #686 everything above stayed on screen unchanged.
        vm.SelectedItem = folderRow;
        await WaitForAsync(() => vm.DetailTitle == childFolder && vm.SysCurrentVersion == "");

        Assert.Equal(childFolder, vm.DetailTitle);
        Assert.Equal(childFolder, vm.SysName);
        Assert.True(vm.DetailIsFolder, "the pane should know its subject is a folder");

        // A folder has no versions, and the pane must SAY so rather than keep the document's number and
        // extension — an inherited field is a claim about the wrong object (ADR 0559).
        Assert.Equal("", vm.SysCurrentVersion);
        Assert.Equal("", vm.SysFileExtension);

        // Its OWN mask — not the document's. This is what caught the race: the document's load was still in
        // flight and finished last, repainting the mask line under a folder's title. A superseded load now
        // stands down (ADR 0559 from the other end: the pane must describe what is selected, not what was).
        Assert.Contains("Mask:", vm.MaskLine);
        Assert.DoesNotContain("Basic Entry", vm.MaskLine);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
