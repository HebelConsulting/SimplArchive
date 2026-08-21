using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The tree opens as the user left it, across sessions.
//
// Two sessions are simulated by building a SECOND MainWindowViewModel against the same store — that is what a
// relaunch is, and asserting against the same view-model would prove only that a HashSet holds what was put in
// it. Each test points the store at its own throwaway file, so a run never touches the developer's real state.
[Collection(UiCollection.Name)]
public class DesktopTreeExpansionMemoryTests : IDisposable
{
    private readonly SelfHostedAppFixture _app;
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"tree-{Guid.NewGuid():N}.json");

    public DesktopTreeExpansionMemoryTests(SelfHostedAppFixture app)
    {
        _app = app;
        TreeExpansionStore.PathOverride = _statePath;
    }

    public void Dispose()
    {
        TreeExpansionStore.PathOverride = null;
        if (File.Exists(_statePath)) File.Delete(_statePath);
    }

    private async Task<MainWindowViewModel> SessionAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl)), SelfHostedAppFixture.AdminEmail);
        return vm;
    }

    private async Task<HttpClient> HttpAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        return http;
    }

    [Fact]
    public async Task A_branch_left_open_is_open_again_next_session()
    {
        var first = await SessionAsync();
        var repo = first.Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });
        await repo.EnsureExpandedAsync();
        var child = repo.Children.First(c => !c.IsSynthetic && !c.IsLauncher);
        await child.EnsureExpandedAsync();

        // A relaunch: a new view-model reading the same file.
        var second = await SessionAsync();
        var repoAgain = second.Tree.First(n => n.Id == repo.Id);

        Assert.True(repoAgain.IsExpanded, "the repository should have reopened");
        Assert.True(repoAgain.Children.First(c => c.Id == child.Id).IsExpanded,
            "the branch inside it should have reopened too — restoring only the top level is not restoring the tree");
    }

    [Fact]
    public async Task A_branch_that_was_closed_stays_closed()
    {
        var first = await SessionAsync();
        var repo = first.Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });
        await repo.EnsureExpandedAsync();
        repo.IsExpanded = false;

        var second = await SessionAsync();
        Assert.False(second.Tree.First(n => n.Id == repo.Id).IsExpanded);
    }

    // The case a naive "drop anything that did not turn up" would get wrong, and the reason the parent is
    // stored: collapsing a node must NOT forget what was open inside it. The restore never walks into a closed
    // branch, so those entries are unverifiable — and unverifiable is not the same as gone.
    [Fact]
    public async Task Collapsing_a_parent_keeps_what_was_open_inside_it()
    {
        var first = await SessionAsync();
        var repo = first.Tree.First(n => n is { IsSynthetic: false, IsLauncher: false, IsPersonal: false });
        await repo.EnsureExpandedAsync();
        var child = repo.Children.First(c => !c.IsSynthetic && !c.IsLauncher);
        await child.EnsureExpandedAsync();
        repo.IsExpanded = false; // shut the branch; the child's state must survive

        var second = await SessionAsync();
        var repoAgain = second.Tree.First(n => n.Id == repo.Id);
        Assert.False(repoAgain.IsExpanded);

        // Re-opening by hand finds the inside as it was left.
        await repoAgain.EnsureExpandedAsync();
        var third = await SessionAsync();
        var repoThird = third.Tree.First(n => n.Id == repo.Id);
        Assert.True(repoThird.IsExpanded);
        Assert.True(repoThird.Children.First(c => c.Id == child.Id).IsExpanded,
            "the descendant's state was discarded when its parent was collapsed");
    }

    // A folder that is genuinely gone IS forgotten — the other half of the pruning rule, and the half that
    // stops the file growing forever.
    [Fact]
    public async Task A_deleted_folder_is_forgotten()
    {
        using var http = await HttpAsync();
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var name = $"tmp{Guid.NewGuid():N}"[..9];
        var doomed = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name, folderMask = "folder" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var first = await SessionAsync();
        var repo = first.Tree.First(n => n.Id == repoId);
        await repo.EnsureExpandedAsync();
        await repo.Children.First(c => c.Id == doomed).EnsureExpandedAsync();

        Assert.Contains(doomed.ToString(), await File.ReadAllTextAsync(_statePath));

        // Every mutation carries If-Match (ADR 0188) — a delete without one is 428, not an oversight to work
        // around. The ETag comes from reading the resource first, which is what a client does anyway.
        var etag = (await http.GetAsync($"/api/documents/{doomed}")).Headers.ETag!.Tag;
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{doomed}");
        delete.Headers.TryAddWithoutValidation("If-Match", etag);
        (await http.SendAsync(delete)).EnsureSuccessStatusCode();

        // The restore opens the repository, does not find the folder among its children, and drops it.
        await SessionAsync();
        Assert.DoesNotContain(doomed.ToString(), await File.ReadAllTextAsync(_statePath));
    }
}
