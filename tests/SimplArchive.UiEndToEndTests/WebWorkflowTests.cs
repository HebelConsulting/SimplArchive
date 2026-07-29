using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0298 + "Workflow start on demand" + "Workflow escalation / SLA reminders"): the seeded demo
// document is In Review, self-assigned to the admin, with a past due date (overdue). Its review shows on the
// Tasks tab with an overdue badge; the workflow is opened on demand via the ribbon "Start workflow" button,
// which opens a separate window where the assigned reviewer Approves then Releases it. (This is the only test
// that mutates the demo document's workflow state; nothing else asserts on it.)
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebWorkflowTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWorkflowTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Pending_review_shows_on_tasks_tab_and_the_workflow_window_approves_then_releases()
    {
        var page = await Ui.LoginAsync(_app);

        // The Tasks tab lists the pending review — with an overdue badge (the demo review's deadline is in the
        // past, ADR "Workflow escalation / SLA reminders").
        await page.Locator(".wb-tab").Filter(new() { HasText = "Tasks" }).First.ClickAsync();
        await Expect(page.GetByText("Invoice 2025-001").First).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-tasks").GetByText("Overdue").First).ToBeVisibleAsync();

        // The task's Open navigates to the document (Repositories tab, doc selected).
        await page.GetByRole(AriaRole.Button, new() { Name = "Open" }).First.ClickAsync();

        // Open the workflow on demand from the ribbon → a separate dialog window.
        await page.GetByRole(AriaRole.Button, new() { Name = "Start workflow" }).ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.Locator(".mud-chip").Filter(new() { HasText = "In Review" })).ToBeVisibleAsync();

        // The admin is the assigned reviewer → Approve, then Release.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Release", Exact = true }).ClickAsync();

        await Expect(dialog.Locator(".mud-chip").Filter(new() { HasText = "Released" })).ToBeVisibleAsync();
    }
}
