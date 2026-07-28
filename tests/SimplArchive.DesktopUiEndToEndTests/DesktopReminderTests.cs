using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of document reminders (ADR "Document reminders"): the real SimplArchiveApiClient sets,
// lists and cancels a reminder, and reads the target catalog.
[Collection(UiCollection.Name)]
public class DesktopReminderTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopReminderTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_list_and_cancel_a_reminder()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var name = $"rem-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, name, Encoding.UTF8.GetBytes("remind me"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(name));

        // The target catalog includes at least the demo admin.
        Assert.NotEmpty(await api.GetReminderTargetsAsync(doc.Id));

        Assert.Empty(await api.GetRemindersAsync(doc.Id));

        // Set a weekly reminder for myself (target null) with a note.
        await api.CreateReminderAsync(doc.Id, DateTimeOffset.UtcNow.AddDays(3), "Follow up", recurrence: 2, targetUserId: null);
        var reminders = await api.GetRemindersAsync(doc.Id);
        var mine = Assert.Single(reminders);
        Assert.Equal("Follow up", mine.Note);
        Assert.Equal(2, mine.Recurrence);

        // A past due date is rejected.
        await Assert.ThrowsAsync<ApiActionException>(() => api.CreateReminderAsync(doc.Id, DateTimeOffset.UtcNow.AddMinutes(-5), null, 0, null));

        // Cancel → the list is empty again.
        await api.CancelReminderAsync(doc.Id, mine.Id);
        Assert.Empty(await api.GetRemindersAsync(doc.Id));
    }
}
