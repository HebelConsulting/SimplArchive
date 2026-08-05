using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// UI flows: the preview full-screen toggle (ADR 0295) maximizes/restores the preview pane, and the app-bar
// dark-mode toggle applies + persists a dark theme.
[Collection(UiCollection.Name)]
public class WebUiTogglesTests
{
    private readonly SelfHostedAppFixture _app;

    public WebUiTogglesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Preview_fullscreen_toggle_maximizes_and_restores()
    {
        var page = await Ui.LoginAsync(_app);

        // Select the seeded document → its preview renders (the seeded invoice PDF via pdf.js), exposing the
        // full-screen control the rest of this test exercises.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await list.GetByText("Contracts").First.DblClickAsync();
        await list.GetByText("Acme Corp").First.DblClickAsync();
        await list.GetByText("Invoice 2026-003").First.ClickAsync();
        await Expect(page.Locator(".wb-pv-fs-btn").First).ToBeVisibleAsync();

        // Maximize → the preview gets the full-screen overlay class; restore removes it.
        await page.Locator(".wb-pv-fs-btn").First.ClickAsync();
        await Expect(page.Locator(".wb-preview.wb-pv-fullscreen")).ToBeVisibleAsync();

        await page.Locator(".wb-pv-fs-btn").First.ClickAsync();
        await Expect(page.Locator(".wb-preview.wb-pv-fullscreen")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Dark_mode_toggle_applies_and_persists()
    {
        var page = await Ui.LoginAsync(_app);
        var before = await page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

        await page.GetByTitle("Toggle light/dark mode").ClickAsync();

        // The theme applies (body background changes) and the choice persists to localStorage.
        await page.WaitForFunctionAsync("before => getComputedStyle(document.body).backgroundColor !== before", before);
        Assert.Equal("True", await page.EvaluateAsync<string?>("() => localStorage.getItem('simplarchive.darkMode')"));
    }
}
