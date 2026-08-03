namespace SimplArchive.UiEndToEndTests;

// Foundation check: the self-hosted real app serves the Blazor WASM client, the interactive OIDC login works
// in a real browser, and the workbench renders. Proves the harness before the behavior tests build on it.
[Collection(UiCollection.Name)]
public class WebSmokeTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSmokeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Logs_in_and_renders_the_workbench()
    {
        var page = await Ui.LoginAsync(_app);

        // The bottom tab bar and the seeded demo repository are visible once the workbench loads. Tabs are
        // icon-only (#298), so identify the Repositories tab by its aria-label rather than visible text.
        await page.Locator(".wb-tab[aria-label='Repositories']").First.WaitForAsync();
        await page.GetByText("Demo Repository").First.WaitForAsync();
    }
}
