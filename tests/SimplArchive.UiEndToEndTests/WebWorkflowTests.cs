using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0298 + "Workflow start on demand" + "Workflow escalation / SLA reminders"): a pending review
// shows on the Tasks tab with its overdue badge, and the workflow window lets the assigned reviewer Approve
// then Release.
//
// SPLIT IN TWO ON PURPOSE, and the reason is worth reading before merging them back.
//
// This file used to do both halves against the SEEDED "Invoice 2026-003", and carried a comment asserting
// "this is the only test that mutates the demo document's workflow state; nothing else asserts on it." That
// was FALSE: WebTasksFilterTests asserts on exactly that task row. Approving and releasing COMPLETES the
// review, the row ceases to exist, and that test then waited 30 s for it and failed.
//
// It was invisible in CI because the suite is split into four legs with four separate databases, so the
// mutation could not reach the reader. Run as one process it failed deterministically — and cost a long hunt
// (#420) that looked like a resource leak and was nothing of the kind. xUnit executes these classes
// REVERSE-ALPHABETICALLY, so WebWorkflowTests runs 2nd and WebTasksFilterTests 26th: position 2 destroyed
// what position 26 needed.
//
// So: the read-only assertions keep using the seeded review (that is what shared fixture data is FOR), and
// the mutating half files its own document and starts its own workflow. CLAUDE.md's standing principle — a
// test that MUTATES data creates its own; only a read-only test may share the seed.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebWorkflowTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWorkflowTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Pending_review_shows_on_the_tasks_tab_with_its_overdue_badge()
    {
        var page = await Ui.LoginAsync(_app);

        // READ-ONLY against the seeded review, which is why it may share it. The overdue badge cannot be
        // reproduced on a self-made document at all: the deadline is seeded in the past (DueAt = now - 1 day)
        // and no user-facing control sets a due date, so this assertion HAS to run against the seed.
        await page.Locator(".wb-tab[aria-label=\"Tasks\"]").First.ClickAsync();
        await Expect(page.GetByText("Invoice 2026-003").First).ToBeVisibleAsync();
        await Expect(page.Locator(".wb-tasks").GetByText("Overdue").First).ToBeVisibleAsync();

        // Every task row carries the In Review status chip (constant by construction — the tasks API only
        // lists reviews — but said, so the state is visible on the tab).
        await Expect(page.Locator(".wb-tasks").GetByText("In Review").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_workflow_window_approves_then_releases_a_document()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "workflowdoc" + Guid.NewGuid().ToString("N")[..8];

        // ITS OWN SUBJECT, filed through the intray the way a user would — unique per run, so a repeated or
        // parallel execution cannot collide with itself either.
        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#intray-file-input", new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("a document this test owns"),
        });

        var intrayRow = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(intrayRow).ToBeVisibleAsync();
        await intrayRow.Locator("button").Last.ClickAsync();
        await page.GetByText("File to folder").First.ClickAsync();

        var filing = page.Locator(".mud-dialog");
        await filing.GetByText("Demo Repository").First.ClickAsync();
        await filing.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true }).ClickAsync();
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();

        // Select it in the repository, then start a workflow on demand — the ribbon button's label is
        // state-aware, and for a document with no workflow it reads "Start workflow".
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        var docRow = page.Locator("[data-pane='list']").GetByText(name).First;
        await Expect(docRow).ToBeVisibleAsync();
        await docRow.ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Start workflow", Exact = true }).ClickAsync();

        var dialog = page.Locator(".mud-dialog");

        // A SELF-MADE document starts in Draft, and that is the one real difference from the seeded review this
        // half used to run against: the seeded document is already In Review, so the old test could assert the
        // chip the moment the dialog opened. Here the workflow has to be STARTED — pick a reviewer, submit —
        // and only then is there an In Review chip to see. Asserting it straight after opening the dialog waits
        // 30 s for a state nothing has asked for yet, which is exactly how this failed when the test was split.
        await Expect(dialog.Locator(".mud-chip").Filter(new() { HasText = "Draft" })).ToBeVisibleAsync();

        // The reviewer select opens via its .mud-input-control, not the hidden combobox input (the MudSelect
        // gotcha in CLAUDE.md). Submit stays disabled until a reviewer is picked, so this is not optional.
        await dialog.Locator(".mud-input-control").First.ClickAsync();
        await page.Locator(".mud-list-item").Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Submit for review", Exact = true }).ClickAsync();

        await Expect(dialog.Locator(".mud-chip").Filter(new() { HasText = "In Review" })).ToBeVisibleAsync();

        // The admin is the assigned reviewer → Approve, then Release. Both mutate, and both now act on a
        // document nothing else in the suite has ever heard of.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Release", Exact = true }).ClickAsync();

        await Expect(dialog.Locator(".mud-chip").Filter(new() { HasText = "Released" })).ToBeVisibleAsync();
    }
}
