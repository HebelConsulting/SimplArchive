using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of workflow review reassignment (ADR "Workflow review reassignment"): the real DesktopClient
// SimplArchiveApiClient drives submit → reassign and the deactivation guard (deactivating a reviewer with a
// pending task is refused unless a replacement is supplied) against the running API. The XAML/VM plumbing is
// exercised by the workflow window; this proves the api-client wiring end to end. Reviewers are made valid by
// granting them IsTenantAdmin (CanReadContent via the tenant-admin ACL bypass), avoiding an ACL-grant api.
[Collection(UiCollection.Name)]
public class DesktopWorkflowReassignTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopWorkflowReassignTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Reassign_action_and_deactivation_guard()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A repository with one confirmed-version document.
        await client.Documents.CreateRepositoryAsync($"dt-wf-{suffix}");
        var repo = (await client.Documents.GetRepositoriesAsync()).First(r => r.Name == $"dt-wf-{suffix}");
        await client.Documents.UploadFileAsync(repo.Id, $"dt-wf-{suffix}.txt", Encoding.UTF8.GetBytes("reassign me"));
        var doc = (await client.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.HasVersions);

        // Two reviewers — tenant admins so they can read the content (valid reviewer targets).
        var u1 = await client.Admin.CreateUserAsync($"dt-r1-{suffix}@example.test", $"Reviewer One {suffix}");
        var u2 = await client.Admin.CreateUserAsync($"dt-r2-{suffix}@example.test", $"Reviewer Two {suffix}");
        await client.Admin.SetRightsAsync(u1, TenantAdmin);
        await client.Admin.SetRightsAsync(u2, TenantAdmin);

        // Submit to U1.
        var wf = await client.Documents.GetWorkflowAsync(doc.Id);
        Assert.NotNull(wf);
        await client.Workflow.PostWorkflowActionAsync(wf!.Links["submit"], new { reviewerId = u1.Id });

        // The reassign link is now offered (the demo admin is an editor); reassign to U2.
        wf = await client.Documents.GetWorkflowAsync(doc.Id);
        Assert.True(wf!.Links.ContainsKey("reassign"));
        await client.Workflow.PostWorkflowActionAsync(wf.Links["reassign"], new { reviewerId = u2.Id });

        wf = await client.Documents.GetWorkflowAsync(doc.Id);
        Assert.Equal("In Review", wf!.StatusName);
        Assert.Equal($"Reviewer Two {suffix}", wf.AssignedToName);

        // Deactivating U2 (who now holds the task) without a replacement is refused.
        await Assert.ThrowsAsync<ReviewerHasPendingReviewsException>(() => client.Admin.DeleteUserAsync(u2));

        // Handing the review back to U1 deactivates U2 and moves the task.
        await client.Admin.DeleteUserAsync(u2, u1.Id);
        wf = await client.Documents.GetWorkflowAsync(doc.Id);
        Assert.Equal($"Reviewer One {suffix}", wf!.AssignedToName);
        Assert.False((await client.Admin.GetUsersAsync()).Single(u => u.Id == u2.Id).IsActive);
    }

    private static readonly AdminClient.SystemRightsData TenantAdmin =
        new(true, false, false, false, false, false, false, false, false, false, false, false, false);
}
