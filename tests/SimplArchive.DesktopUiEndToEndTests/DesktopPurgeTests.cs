using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Permanent purge (ADR "Manual hard-delete / purge") via the real desktop api client: the demo admin (a tenant
// administrator) creates a throwaway folder, deletes it, then purges it from the recycle bin — after which it's
// gone from the recycle-bin listing.
[Collection(UiCollection.Name)]
public class DesktopPurgeTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopPurgeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Purge_removes_a_recycle_bin_item()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        var folderName = $"Purge {suffix}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == folderName);

        // Delete it, then purge it permanently.
        await api.DeleteAsync(folder.Id);
        var recycled = await api.GetRecycleBinAsync(repo);
        Assert.Contains(recycled, i => i.Id == folder.Id);

        await api.PurgeAsync(recycled.Single(i => i.Id == folder.Id));
        Assert.DoesNotContain(await api.GetRecycleBinAsync(repo), i => i.Id == folder.Id);
    }
}
