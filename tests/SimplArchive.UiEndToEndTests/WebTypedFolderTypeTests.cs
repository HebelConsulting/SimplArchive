using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of #824 (DesktopTypedFolderTypeTests is the sibling): a typed folder's Type cell names its
// mask, and only the plain folder keeps the generic word. Asserted on the personal space, whose first level
// is provisioned typed folders — stable rows on every demo stack.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebTypedFolderTypeTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTypedFolderTypeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_typed_folder_names_its_mask_and_a_plain_folder_stays_Folder()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");

        // The personal space: its provisioned first level is all typed folders. Addressed inside the TREE
        // pane — "Demo Admin" also appears in the list's Owner column, and a bare GetByText would race
        // whichever rendered first.
        await page.Locator("[data-pane='tree']").GetByText("Demo Admin").First.ClickAsync();
        var addressbook = list.Locator(".wb-list-row").Filter(new() { HasText = "My Addressbook" }).First;
        await Expect(addressbook.Locator(".wb-ccell").Nth(1)).ToHaveTextAsync("Addressbook");
        var calendar = list.Locator(".wb-list-row").Filter(new() { HasText = "My Calendar" }).First;
        await Expect(calendar.Locator(".wb-ccell").Nth(1)).ToHaveTextAsync("Calendar");

        // …while an ordinary repository folder keeps the generic word (localised; the suite runs in English).
        await page.GetByText("Demo Repository").First.ClickAsync();
        var contracts = list.Locator(".wb-list-row").Filter(new() { HasText = "Contracts" }).First;
        await Expect(contracts.Locator(".wb-ccell").Nth(1)).ToHaveTextAsync("Folder");
    }
}
