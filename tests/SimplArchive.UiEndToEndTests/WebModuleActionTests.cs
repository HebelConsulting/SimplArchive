using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// The web client renders a module's pick-then-act action, commits it, and shows a value revealed once in the
/// copy-once dialog (#1628, ADR 0786, ABI 1.8), as the desktop does.
/// </summary>
[Collection(ModuleUiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebModuleActionTests(ModuleAppFixture app)
{
    private static readonly Guid DossierMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000000");

    [Fact]
    public async Task A_module_action_commits_and_its_revealed_value_is_shown_once_to_copy()
    {
        var dossierName = $"Dossier {Guid.NewGuid():N}"[..20];
        var dossierId = await ActivatedModuleWithADossierAsync(dossierName);

        var page = await Ui.LoginAsync(app);
        await page.GotoAsync($"{app.BaseUrl}/go/{dossierId}");
        await Expect(page.Locator(".wb-detail-head")).ToContainTextAsync(dossierName, new() { Timeout = 60000 });

        // The module's button, with its own label.
        var button = page.Locator("[data-module-action='test-module:issue']");
        await Expect(button).ToHaveTextAsync("Issue a secret");
        await button.ClickAsync();

        // The picker: the action's one option comes pre-chosen, so the commit is one click.
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByText("Issue it?")).ToBeVisibleAsync();
        await dialog.Locator("[data-module-commit='test-module:issue']").ClickAsync();

        // The value, shown once, with the copy-once hint and nowhere else.
        var reveal = page.Locator(".mud-dialog").Filter(new() { HasText = "Copy it now: it won't be shown again." });
        await Expect(reveal).ToBeVisibleAsync();
        await Expect(reveal.Locator("input")).ToHaveValueAsync("s3cret-shown-once");
        await reveal.GetByRole(AriaRole.Button, new() { Name = "Done" }).ClickAsync();
        await Expect(reveal).Not.ToBeVisibleAsync();
    }

    // The test module activated in the demo tenant, and a Test Dossier to act on, through the API as the demo admin.
    private async Task<Guid> ActivatedModuleWithADossierAsync(string dossierName)
    {
        var (http, _, repoId) = await app.ActivateTestModuleAsync();
        using var client = http;
        return (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = dossierName, maskId = DossierMaskId }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
