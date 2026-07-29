using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0289): the single Edit toggle makes the detail pane editable and Save persists it. Uploads its
// own document (independent of the seeded content), then in one edit changes the name and assigns a mask via the
// mask picker, and confirms both persist in the read-only view.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebDetailEditTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDetailEditTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Editing_the_name_and_assigning_a_mask_persists()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");

        var name = "editme-" + Guid.NewGuid().ToString("N")[..8];
        var renamed = "renamed-" + Guid.NewGuid().ToString("N")[..8];

        // Upload a document into the repository and select it.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("edit me") });
        await list.GetByText(name).First.ClickAsync();

        // Edit: rename + assign the Basic Entry mask via the picker.
        await index.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await FillMudAsync(index.Locator(".wb-sysfields tr", new() { HasText = "Name" }).Locator("input"), renamed);

        await index.Locator(".wb-mask-edit .mud-input-control").First.ClickAsync(); // open the mask picker
        await page.Locator(".mud-list-item").Filter(new() { HasText = "Basic Entry" }).First.ClickAsync();

        await index.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // Both changes persist in the read-only view after reselecting.
        await Expect(list.GetByText(renamed)).ToBeVisibleAsync();
        await list.GetByText(renamed).First.ClickAsync();
        await Expect(index).ToContainTextAsync(renamed);
        await Expect(index.GetByText("Mask: Basic Entry")).ToBeVisibleAsync();
    }

    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()"); // MudTextField commits on blur (no Immediate)
    }
}
