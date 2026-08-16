using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the notification viewer (ADR "Notification viewer + click-through"): the real
// SimplArchiveApiClient reads a user's notifications (with the document's parent folder for click-through) and
// marks one read. Set up by the demo admin submitting a document to a fresh reviewer, who then reads their inbox.
[Collection(UiCollection.Name)]
public class DesktopNotificationsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopNotificationsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Reviewer_reads_notifications_with_parent_and_marks_read()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A repository + a confirmed-version document.
        await admin.CreateRepositoryAsync($"dt-notif-{suffix}");
        var repo = (await admin.GetRepositoriesAsync()).First(r => r.Name == $"dt-notif-{suffix}");
        await admin.UploadFileAsync(repo.Id, $"notif-{suffix}.txt", Encoding.UTF8.GetBytes("hi"));
        var doc = (await admin.GetChildrenAsync(repo.Href("children"))).First(c => c.HasVersions);

        // A fresh reviewer (tenant admin so they can read the content), with a password to log in.
        var email = $"dt-reviewer-{suffix}@example.test";
        var reviewerId = await admin.Admin.CreateUserAsync(email, $"DT Reviewer {suffix}");
        await admin.Admin.SetRightsAsync(reviewerId, new AdminClient.SystemRightsData(true, false, false, false, false, false, false, false, false, false, false, false, false));
        var password = await admin.Admin.ResetUserPasswordAsync(reviewerId);

        // Submit the document to the reviewer → a ReviewAssigned notification for them.
        var wf = await admin.GetWorkflowAsync(doc.Id);
        await admin.Workflow.PostWorkflowActionAsync(wf!.Links["submit"], new { reviewerId = reviewerId.Id });

        // The reviewer reads their inbox: the notification carries the document + its parent folder.
        var reviewer = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, email, password));
        var list = await reviewer.Notifications.GetNotificationsAsync();
        Assert.True(list.UnreadCount >= 1);
        var n = list.Items.First(x => x.DocumentId == doc.Id);
        Assert.Equal(repo.Id, n.DocumentParentId);
        Assert.False(n.IsRead);

        // Marking it read drops the unread count.
        await reviewer.Notifications.MarkNotificationReadAsync(n);
        var after = await reviewer.Notifications.GetNotificationsAsync();
        Assert.True(after.Items.First(x => x.Id == n.Id).IsRead);
        Assert.Equal(list.UnreadCount - 1, after.UnreadCount);
    }
}
