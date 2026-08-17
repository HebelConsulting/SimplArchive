using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of folder / subtree subscriptions (ADR "Folder / subtree subscriptions"): a folder (a
// Document with no versions) can be followed/unfollowed through the real SimplArchiveApiClient — the same
// endpoint as a document — backing the tree context-menu "Follow / unfollow this folder" action.
[Collection(UiCollection.Name)]
public class DesktopFolderSubscriptionTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopFolderSubscriptionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Follow_and_unfollow_a_folder_round_trips()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"watched-{Guid.NewGuid():N}";
        await api.Documents.CreateFolderAsync(repo.Href("children"), name);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == name);

        Assert.False(await api.Documents.GetSubscriptionAsync(await api.Documents.RelViaSelfAsync(folder.Href("self"), "subscription"))); // not following by default

        await api.Documents.SetSubscriptionAsync(await api.Documents.RelViaSelfAsync(folder.Href("self"), "subscription"), subscribe: true);
        Assert.True(await api.Documents.GetSubscriptionAsync(await api.Documents.RelViaSelfAsync(folder.Href("self"), "subscription")));

        await api.Documents.SetSubscriptionAsync(await api.Documents.RelViaSelfAsync(folder.Href("self"), "subscription"), subscribe: false);
        Assert.False(await api.Documents.GetSubscriptionAsync(await api.Documents.RelViaSelfAsync(folder.Href("self"), "subscription")));
    }
}
