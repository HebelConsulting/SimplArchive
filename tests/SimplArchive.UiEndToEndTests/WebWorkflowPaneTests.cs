using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The detail pane's workflow slot shows the transitions the SERVER advertises, or nothing (#691).
//
// What it replaced was a permanent button labelled from the status string — Start / Manage / View — sitting in
// the row people use constantly for an action most documents never take. The slot now earns its place: empty
// when there is no workflow, and holding the actual next steps when there is one.
//
// ONE test walking the states in order, on a document it seeds ITSELF. That is not tidiness: the demo seed's
// invoice carries the only ready-made review, and WebWorkflowTests already approves and releases it, so a
// second test reading "In Review" from the same document passes or fails on which ran first. Written that way
// once, and it failed exactly so.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebWorkflowPaneTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWorkflowPaneTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_slot_is_empty_without_a_workflow_and_offers_the_state_transitions_with_one()
    {
        var name = $"wf-{Guid.NewGuid():N}"[..11];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var versionId = await AddVersionAsync(http, docId);

        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var pane = page.Locator("[data-pane='index']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name }).First;
        await row.ClickAsync();

        // ── No workflow: the state is stated, and the slot beside it is EMPTY. The absence is the load-bearing
        //    half — a pane that rendered every transition unconditionally would put Approve on every document
        //    in the archive and still satisfy a test that only looked at the In Review case. ────────────────
        await Expect(pane.GetByText("not started")).ToBeVisibleAsync();
        foreach (var action in new[] { "Approve", "Reject", "Submit for review", "Release" })
        {
            await Expect(pane.GetByRole(AriaRole.Button, new() { Name = action, Exact = true })).ToHaveCountAsync(0);
        }

        // ── Submitted to ourselves, so the signed-in admin is the assigned reviewer and the choice is real.
        //    Submitting through the API rather than the UI on purpose: starting a workflow stays a context-menu
        //    action (#691), so the pane is not where that happens. ──────────────────────────────────────────
        var me = (await http.GetFromJsonAsync<JsonElement>("/api/diagnostics/whoami")).GetProperty("userId").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/workflow/submit", new { reviewerId = me }))
            .EnsureSuccessStatusCode();

        await row.ClickAsync(); // reselect so the pane reloads its transitions
        await Expect(pane.GetByText("In Review")).ToBeVisibleAsync();

        // The reviewer's actual choices, in the pane, rather than two clicks away inside a dialog.
        foreach (var action in new[] { "Approve", "Reject" })
        {
            await Expect(pane.GetByRole(AriaRole.Button, new() { Name = action, Exact = true })).ToBeVisibleAsync();
        }

        // ── Approving ACTS. No dialog offering the same button again — that was the old affordance and what
        //    #691 called an invitation wearing an action's label. ───────────────────────────────────────────
        await pane.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();

        await Expect(pane.GetByText("Approved")).ToBeVisibleAsync();
        await Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);

        // ...and the slot now offers what the NEW state affords. This is what proves the buttons are re-read
        // from the server after acting, rather than a fixed set drawn once per document.
        await Expect(pane.GetByRole(AriaRole.Button, new() { Name = "Release", Exact = true })).ToBeVisibleAsync();
        await Expect(pane.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true })).ToHaveCountAsync(0);
    }

    private static async Task<Guid> AddVersionAsync(HttpClient http, Guid docId)
    {
        var created = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var versionId = created.GetProperty("id").GetGuid();

        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("workflow subject\n")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();
        return versionId;
    }
}
