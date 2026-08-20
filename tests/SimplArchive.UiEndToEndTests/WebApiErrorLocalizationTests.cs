using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of issue #424: with the app running in German, a refusal that comes from the SERVER must reach
// the user in German. It did not — the API's Problem Details `detail` is English (its 153 exception classes
// carry their message as a constructor literal, so no Accept-Language handling can reach them) and the client
// displayed it verbatim, so a German user got German until something went wrong and English exactly when it
// mattered. The client now maps the machine `errorCode` to its own localised text (ApiErrorText).
//
// The refusal is real, not mocked, and it is a race a user can genuinely lose: the workflow dialog captures its
// transition hrefs when it opens, so if the document moves on underneath it, the button it is still showing no
// longer applies and the server answers WORKFLOW_TRANSITION_NOT_ALLOWED. The desktop suite asserts the same
// guarantee on a different refusal (DesktopApiErrorLocalizationTests, a legal-held index-data edit) — one
// guarantee, two clients.
//
// It used to provoke CANNOT_CHANGE_ROOT_INHERITANCE by breaking inheritance on a repository root. That is no
// longer reachable from a conforming client: the server stopped advertising the acl-inheritance rel on a root,
// so the toggle is not drawn at all (#426). Re-pointed rather than deleted — what is under test is the
// LANGUAGE, not which refusal happens to carry it.
//
// Asserts the German sentence itself rather than "different from the English one": an inequality assertion
// passes just as happily on an empty message or on the generic fallback, the two most likely ways this breaks.
[Collection(UiCollection.Name)]
public class WebApiErrorLocalizationTests
{
    private readonly SelfHostedAppFixture _app;

    public WebApiErrorLocalizationTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_server_refusal_reaches_the_user_in_their_language()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"wf-i18n-{suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        // A throwaway document with a confirmed version — a workflow needs one, and the seeded demo document's
        // workflow belongs to WebWorkflowTests, so using it would make these two order-dependent.
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository")
            .GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var versionId = version.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("workflow i18n")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-langbtn").ClickAsync();
        await page.GetByText("Deutsch").First.ClickAsync();
        await Expect(page.Locator(".wb-tab").First).ToHaveAttributeAsync("aria-label", "Archive", new() { Timeout = 25000 });

        // Open the workflow dialog on the fresh document, while it is still Draft.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = page.Locator("[data-pane='list']").Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();
        // Exact, because two buttons legitimately carry this name: the ribbon's "Workflow starten"
        // (RibbonStartWorkflow) and the detail pane's "Workflow starten…" (CtxStartWorkflow, with an ellipsis).
        // Playwright's `Name` is a SUBSTRING match unless told otherwise, so the shorter one matches both — and
        // only once the detail pane has finished rendering, which makes it a race the test wins on a fast
        // machine and loses on a slow runner. It failed on `main` exactly that way, as a strict-mode violation
        // rather than a timeout, which is the tell: two elements, not zero.
        await page.GetByRole(AriaRole.Button, new() { Name = "Workflow starten", Exact = true }).ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        // Submit stays disabled until a reviewer is chosen, so pick one (the MudSelect opens via its
        // input-control, not the hidden combobox input — the standing gotcha in CLAUDE.md).
        await dialog.Locator(".mud-input-control").First.ClickAsync();
        await page.Locator(".mud-list-item").First.ClickAsync();

        var submit = dialog.GetByRole(AriaRole.Button, new() { Name = "Zur Prüfung einreichen" });
        await Expect(submit).ToBeEnabledAsync();

        // Out of band, the document moves on — someone else submits it for review. The dialog still shows the
        // Draft transition, holding an href that no longer applies.
        var me = await http.GetFromJsonAsync<JsonElement>("/api/diagnostics/whoami");
        var reviewerId = me.GetProperty("userId").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/workflow/submit",
            new { reviewerId })).EnsureSuccessStatusCode();

        // Clicking the stale button is refused — and the user reads it in German.
        await submit.ClickAsync();

        var snackbar = page.Locator(".mud-snackbar");
        await Expect(snackbar).ToContainTextAsync(
            "Dieser Workflow-Schritt ist aus dem aktuellen Status nicht zulässig.");

        // Never the API's English prose, and never the generic fallback — the code is mapped, so the message is
        // about THIS refusal.
        await Expect(snackbar).Not.ToContainTextAsync("not allowed");
        await Expect(snackbar).Not.ToContainTextAsync("Die Aktion wurde vom Server abgelehnt.");
    }
}
