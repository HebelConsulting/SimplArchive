using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Repository import (ADR "Repository import") via the real desktop api client: the demo admin creates a
// repository, exports it, imports the archive back as a new repository, and the imported root is listed — all
// through the real SimplArchiveApiClient against the running Api.
[Collection(UiCollection.Name)]
public class DesktopImportTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopImportTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Export_then_import_creates_a_new_repository()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repoName = $"Desktop import {Guid.NewGuid():N}";
        await api.CreateRepositoryAsync(repoName);
        var repoId = (await api.GetRepositoriesAsync()).Single(r => r.Name == repoName).Id;
        await api.UploadFileAsync(repoId, "report.txt", System.Text.Encoding.UTF8.GetBytes($"import-{Guid.NewGuid():N}"));

        var zip = await api.ExportRepositoryAsync(repoId, new SimplArchiveApiClient.RepositoryExportOptions(false, null, null, null, null, null));

        // Import as a new repository (targetFolderId == null). The root name collides with the original, so it's
        // auto-renamed ("… (imported)").
        var result = await api.ImportRepositoryAsync(null, zip);
        Assert.StartsWith(repoName, result.RootName);
        Assert.True(result.Documents >= 1);
        Assert.Equal(1, result.Versions);
        Assert.NotEqual(repoId, result.RootId);

        // The imported repository is now listed alongside the original (auto-renamed since the name collides).
        var repos = await api.GetRepositoriesAsync();
        Assert.Contains(repos, r => r.Id == result.RootId);
    }
}
