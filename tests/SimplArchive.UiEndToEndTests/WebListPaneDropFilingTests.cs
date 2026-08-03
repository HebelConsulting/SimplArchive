using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// List-pane drop filing (ADR "List-pane drop filing"): dropping OS files onto a document row opens the
// inbox-style filing dialog; choosing "file as a new version" adds a version to that document. Simulates the
// drop with a synthetic DataTransfer carrying a File (Playwright's real DnD can't originate an OS file).
[Collection(UiCollection.Name)]
public class WebListPaneDropFilingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebListPaneDropFilingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Dropping_a_file_on_a_document_files_it_as_a_new_version()
    {
        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");

        // Upload a document (version 1) and select it.
        var name = "dropfile-" + Guid.NewGuid().ToString("N")[..8];
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes("v1") });

        var row = list.Locator($"[data-drop-doc]").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();
        await Expect(index.Locator(".wb-current-version")).ToHaveTextAsync("1");

        // Dispatch a synthetic drop of a single File onto the document row → the filing dialog opens.
        var dataTransfer = await page.EvaluateHandleAsync(@"() => {
            const dt = new DataTransfer();
            dt.items.add(new File(['v2 content'], 'dropped.txt', { type: 'text/plain' }));
            return dt;
        }");
        await row.DispatchEventAsync("drop", new Dictionary<string, object> { ["dataTransfer"] = dataTransfer });

        // The document-drop wiring (data-drop-doc → BeginDocumentDropAsync) shows the filing dialog with the
        // as-version option (proving a single-file document-drop context). Pick it explicitly, then file.
        var asVersion = page.GetByText("File as a new version of the selected document");
        await Expect(asVersion).ToBeVisibleAsync();
        await asVersion.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "File", Exact = true }).ClickAsync();

        // The document now has version 2 — confirmed after re-selecting it.
        await Expect(list.GetByText(name).First).ToBeVisibleAsync();
        await list.GetByText(name).First.ClickAsync();
        await Expect(index.Locator(".wb-current-version")).ToHaveTextAsync("2");
    }
}
