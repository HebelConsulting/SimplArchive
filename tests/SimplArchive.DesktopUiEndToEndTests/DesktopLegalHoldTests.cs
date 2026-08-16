using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// Legal hold (ADR "Legal hold & retention enforcement") end to end via the real desktop api client: the demo
// admin (granted CanLegalHold by the demo seed) creates a hold covering a throwaway folder; while held the
// folder can't be deleted; after release it can. Exercises the real SimplArchiveApiClient legal-hold methods
// plus the server-side enforcement.
[Collection(UiCollection.Name)]
public class DesktopLegalHoldTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopLegalHoldTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Place_hold_freezes_deletion_until_released()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.Documents.GetRepositoriesAsync())[0];
        var folderName = $"Hold {suffix}";
        await api.Documents.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == folderName);

        // Place a hold covering the folder; it shows on the hold + the folder reports it.
        var hold = await api.CreateLegalHoldAsync($"Matter {suffix}", "test");
        await api.AddLegalHoldItemAsync(hold, folder.Id);
        var fetched = await api.GetLegalHoldAsync(hold);
        Assert.Contains(fetched.Items, i => i.DocumentId == folder.Id);

        // Frozen: deletion is refused (409 → the client throws).
        await Assert.ThrowsAnyAsync<Exception>(() => api.Documents.DeleteAsync(folder.Id));

        // Release → the hold is no longer active and the folder can be deleted.
        await api.ReleaseLegalHoldAsync(hold);
        Assert.False((await api.GetLegalHoldAsync(hold)).IsActive);
        await api.Documents.DeleteAsync(folder.Id); // succeeds now (also cleans up)
    }
}
