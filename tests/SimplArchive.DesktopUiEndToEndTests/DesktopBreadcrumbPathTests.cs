using System.Net.Http.Json;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The breadcrumb names the folder's REAL path, however deep it sits (#1266).
//
// WHAT WAS WRONG. `OpenLoadedFolderAsync` added exactly two crumbs — `Repositories / <folder>` — regardless of
// depth, so a folder four levels down reported a two-level path. That is not merely incomplete: it says the
// folder is a child of the repositories root, which is a WRONG answer to the question the breadcrumb exists to
// answer (ADR 0703). The original comment blamed the read API for not exposing ancestry; that was stale, as it
// was for the tree half in #1264.
//
// THE ASSERTION IS THE NAMES IN ORDER, not the count. A count-only check passes on a wrong-but-longer path,
// which is the failure mode a breadcrumb actually has — it is a claim about WHERE something is, and a
// plausible wrong path is worse than a short one.
[Collection(UiCollection.Name)]
public class DesktopBreadcrumbPathTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopBreadcrumbPathTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Opening_a_deeply_filed_folder_names_every_ancestor_in_order()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new SimplArchiveApiClient(token);

        // Built here rather than reusing seeded data: this test MUTATES nothing, but it needs a known depth,
        // and a shared folder's depth is not ours to depend on.
        var run = Guid.NewGuid().ToString("N")[..8];
        var (deepestId, deepestHref, names) = await BuildNestedFoldersAsync(token, run);

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // The payload-row entry point — what "Go to …", the references dialog, a task, a notification and a
        // reminder all funnel through. Fixing only the reveal path would leave every one of these short.
        await vm.OpenFolderAsync(deepestHref);
        await WaitForAsync(() => vm.Breadcrumbs.Count > 2, seconds: 25);

        var crumbs = vm.Breadcrumbs.Select(b => b.Name).ToList();

        // Repositories, then the repository, then each folder down to the one opened.
        var expected = new List<string> { "Repositories" };
        expected.AddRange(names);
        Assert.Equal(expected, crumbs);

        // And the crumbs are CLICKABLE: a name without an address is a breadcrumb that throws when used, which
        // is why the ancestors listing had to start advertising one (ADR 0543 — the client may not compose it).
        foreach (var crumb in vm.Breadcrumbs.Skip(1))
        {
            Assert.NotNull(crumb.Links);
            Assert.True(crumb.FolderId.HasValue);
        }

        Assert.Equal(deepestId, vm.Breadcrumbs[^1].FolderId);
    }

    // repo / Level A <run> / Level B <run> / Level C <run>
    private async Task<(Guid DeepestId, string DeepestHref, List<string> Names)> BuildNestedFoldersAsync(string token, string run)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var repoName = $"Crumbs {run}";
        var repo = await PostAsync(http, "/api/repositories", new { name = repoName });
        var names = new List<string> { repoName };

        var parentId = repo.Id;
        var href = repo.Href;
        foreach (var level in new[] { "Level A", "Level B", "Level C" })
        {
            var folderName = $"{level} {run}";
            var created = await PostAsync(http, $"/api/documents/{parentId}/children", new { name = folderName, isFolder = true });
            names.Add(folderName);
            parentId = created.Id;
            href = created.Href;
        }

        return (parentId, href, names);
    }

    private static async Task<(Guid Id, string Href)> PostAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = json.GetProperty("id").GetGuid();
        return (id, $"/api/documents/{id}");
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

        Assert.True(condition(), "condition not met within the deadline");
    }
}
