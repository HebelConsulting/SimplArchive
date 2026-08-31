using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0265/0286): upload a file to the intray, then File it into a repository folder via the filing
// dialog's folder picker — it leaves the intray and appears as a document in that folder.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebIntrayFilingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebIntrayFilingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Intray_item_can_be_filed_into_a_repository_folder()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "intraydoc" + Guid.NewGuid().ToString("N")[..8];
        var fileName = name + ".txt";

        // Intray: upload a file → it appears in the intray list.
        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#intray-file-input", new FilePayload
        {
            Name = fileName,
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("filed via the intray"),
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        // File it via the row's ⋮ menu → filing dialog (nothing selected on Repositories → folder-pick mode).
        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("File to folder").First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByText("Demo Repository").First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true }).ClickAsync();

        // It leaves the intray...
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();

        // ...and shows up as a document in the target repository (named after the file stem, ADR 0277).
        await page.Locator(".wb-tab[aria-label=\"Repositories\"]").First.ClickAsync();
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(page.Locator("[data-pane='list']").GetByText(name)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_filing_dialog_does_not_offer_a_typed_collection_as_a_target()
    {
        // As reported: the dialog listed "My Addressbook" and "My Calendar" as places to file a document. Both
        // are typed collections — contacts and appointments — so the server refuses a plain child there, and
        // the user found out by pressing File. Every row already carried `create-child`; the dialog just never
        // read it (ADR 0543). The desktop picker has gated on that rel since #873 (ADR 0511: one surface).
        var page = await Ui.LoginAsync(_app);
        var name = "typedtarget" + Guid.NewGuid().ToString("N")[..8];

        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        await page.SetInputFilesAsync("#intray-file-input", new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("a document, not a contact and not an appointment"),
        });
        var row = page.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();

        await row.Locator("button").Last.ClickAsync();
        await page.GetByText("File to folder").First.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        var file = dialog.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true });

        // Expand the personal space — the typed collections live at its first level.
        await dialog.Locator(".mud-treeview-item-arrow button").First.ClickAsync();

        // Shown, because a user may need to browse THROUGH it (ADR 0689) — but never a target.
        foreach (var typed in new[] { "My Addressbook", "My Calendar" })
        {
            var node = dialog.GetByText(typed, new() { Exact = true }).First;
            await Expect(node).ToBeVisibleAsync();
            await node.ClickAsync();
            await Expect(file).ToBeDisabledAsync();
        }

        // The control: a plain folder alongside them stays a real target, so this cannot pass by disabling
        // everything.
        await dialog.GetByText("My Documents", new() { Exact = true }).First.ClickAsync();
        await Expect(file).ToBeEnabledAsync();
    }
}
