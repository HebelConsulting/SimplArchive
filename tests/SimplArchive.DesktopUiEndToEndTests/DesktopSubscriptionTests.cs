using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of document subscriptions (ADR "Document subscriptions"): the real SimplArchiveApiClient
// follows/unfollows a document and reads the current state back.
[Collection(UiCollection.Name)]
public class DesktopSubscriptionTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSubscriptionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Follow_and_unfollow_round_trips()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"sub-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("follow me"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        Assert.False(await api.GetSubscriptionAsync(doc.Id)); // not following by default

        await api.SetSubscriptionAsync(doc.Id, subscribe: true);
        Assert.True(await api.GetSubscriptionAsync(doc.Id));

        // Idempotent: following again stays true.
        await api.SetSubscriptionAsync(doc.Id, subscribe: true);
        Assert.True(await api.GetSubscriptionAsync(doc.Id));

        await api.SetSubscriptionAsync(doc.Id, subscribe: false);
        Assert.False(await api.GetSubscriptionAsync(doc.Id));
    }
}
