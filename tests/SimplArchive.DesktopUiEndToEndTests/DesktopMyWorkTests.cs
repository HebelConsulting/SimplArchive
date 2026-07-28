using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the "My work" dashboard (ADR "My work dashboard"): the real SimplArchiveApiClient reads
// the caller's due-soon reminders + followed documents (the two cross-document dashboard lists).
[Collection(UiCollection.Name)]
public class DesktopMyWorkTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopMyWorkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dashboard_lists_due_soon_reminders_and_followed_documents()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"mywork-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("dashboard"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        // A due-soon reminder + following the document.
        await api.CreateReminderAsync(doc.Id, DateTimeOffset.UtcNow.AddDays(1), "Dashboard check", recurrence: 0, targetUserId: null);
        await api.SetSubscriptionAsync(doc.Id, subscribe: true);

        var reminders = await api.GetDashboardRemindersAsync();
        Assert.Contains(reminders, r => r.DocumentId == doc.Id && r.ParentId == repo.Id && r.Note == "Dashboard check");

        var following = await api.GetDashboardFollowingAsync();
        Assert.Contains(following, f => f.DocumentId == doc.Id && f.ParentId == repo.Id);
    }
}
