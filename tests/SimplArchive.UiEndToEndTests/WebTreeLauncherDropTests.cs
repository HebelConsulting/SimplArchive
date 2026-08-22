using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The Personal ▸ Intray and Personal ▸ Check-out tree launchers as drop targets (#467).
//
// Both used to ADVERTISE a drop and do nothing: they are synthetic nodes with Guid.Empty as their id, so the
// generic folder branch handed them data-drop-folder="00000000-…" and every drop 404'd. An inert drop zone is
// worse than none — the user concludes the feature is broken rather than absent — so each is now either a real
// target or not a target at all.
//
// A real OS file drag is impossible in a headless browser, so the drop is synthesized with a DataTransfer, the
// same way the other drop tests do it.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebTreeLauncherDropTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTreeLauncherDropTests(SelfHostedAppFixture app) => _app = app;

    // Expanding Personal is what reveals the launchers — clicking the node SELECTS it, which is a different
    // gesture (and the one that switches tabs).
    private static async Task<ILocator> ExpandPersonalAsync(IPage page)
    {
        var tree = page.Locator("[data-pane='tree']");
        var personal = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First;
        await Expect(personal).ToBeVisibleAsync();
        await personal.Locator(".mud-treeview-item-arrow").ClickAsync();
        return tree;
    }

    private static Task<IJSHandle> FileTransferAsync(IPage page, string fileName, string body) =>
        page.EvaluateHandleAsync(
            @"a => { const dt = new DataTransfer();
                     dt.items.add(new File([a.body], a.name, { type: 'text/plain' }));
                     return dt; }",
            new { name = fileName, body });

    [Fact]
    public async Task Dropping_a_file_on_the_intray_launcher_files_it_and_opens_the_intray()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "treedrop-" + Guid.NewGuid().ToString("N")[..8];

        var tree = await ExpandPersonalAsync(page);
        var intray = tree.Locator("[data-drop-intray]").First;
        await Expect(intray).ToBeVisibleAsync();

        var dt = await FileTransferAsync(page, name + ".txt", "filed via the tree launcher");
        await intray.DispatchEventAsync("dragover", new Dictionary<string, object> { ["dataTransfer"] = dt });
        await intray.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dt });

        // The tree shows FOLDERS, so it can never show what just landed — which is why the drop opens the tab
        // that can. Without this the user is left staring at a node that cannot confirm anything.
        await Expect(page.Locator(".wb-intray-drop")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(page.Locator(".wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    [Fact]
    public async Task Dropping_a_file_that_matches_no_checked_out_document_is_refused_with_a_reason()
    {
        var page = await Ui.LoginAsync(_app);

        var tree = await ExpandPersonalAsync(page);
        var checkout = tree.Locator("[data-drop-checkout]").First;
        await Expect(checkout).ToBeVisibleAsync();

        // A working copy belongs to ONE document, and the filename is what says which. A file naming nothing
        // checked out must be refused OUT LOUD: silence here is exactly how the reminder bug (#420) hid for
        // months — the user acts, nothing happens, and they conclude the feature is broken.
        var dt = await FileTransferAsync(page, "not-checked-out-" + Guid.NewGuid().ToString("N")[..8] + ".txt", "x");
        await checkout.DispatchEventAsync("dragover", new Dictionary<string, object> { ["dataTransfer"] = dt });
        await checkout.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dt });

        await Expect(page.GetByText("does not match a document you have checked out")).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    [Fact]
    public async Task Dragging_a_document_onto_the_intray_copies_it_as_a_template_with_its_index_data()
    {
        var page = await Ui.LoginAsync(_app);

        // Upload a document to drag, rather than depending on where the demo data happens to put one — the
        // repository root holds folders, and a folder has no version to copy (the server rightly refuses it).
        var sourceName = "template-" + Guid.NewGuid().ToString("N")[..8];
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload
        {
            Name = sourceName + ".txt",
            MimeType = "text/plain",
            Buffer = System.Text.Encoding.UTF8.GetBytes("template source"),
        });

        var source = page.Locator("[data-pane='list'] [data-drop-doc]").Filter(new() { HasText = sourceName }).First;
        await Expect(source).ToBeVisibleAsync(new() { Timeout = 15000 });

        var tree = await ExpandPersonalAsync(page);
        var intray = tree.Locator("[data-drop-intray]").First;
        await Expect(intray).ToBeVisibleAsync();

        // An INTERNAL drag carries the app's own MIME type rather than files; dropping it on a folder moves or
        // references, and on the Intray launcher it copies the document in as a template.
        var nodeId = (await source.GetAttributeAsync("data-node-id"))!;
        var dt = await page.EvaluateHandleAsync(
            @"id => { const dt = new DataTransfer();
                      dt.setData('application/x-simplarchive-node', id + '|false');
                      return dt; }",
            nodeId);
        await intray.DispatchEventAsync("dragover", new Dictionary<string, object> { ["dataTransfer"] = dt });
        await intray.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dt });

        // It lands in the Intray, and the Intray tab opens to show it. Crucially it is NOT square-bracketed:
        // brackets mean "un-classified", and a template that arrived without its mask would show them.
        await Expect(page.Locator(".wb-intray-drop")).ToBeVisibleAsync(new() { Timeout = 15000 });
        var staged = page.Locator(".wb-list-row").Filter(new() { HasText = sourceName });
        await Expect(staged).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(staged.First).Not.ToContainTextAsync($"[{sourceName}]");
    }
}
