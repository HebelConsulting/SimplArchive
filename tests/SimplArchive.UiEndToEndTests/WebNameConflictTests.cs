using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Filing a file whose name is already used in the target folder (ADR "A name conflict on filing is a question,
// not a refusal"). It used to return 409, show a warning the user had usually stopped looking at, and drop the
// file — so filing appeared to do nothing at all. It now asks, and both answers are exercised here because they
// produce opposite outcomes on the same folder: one more version of one document, or one more document.
//
// Driven through the ribbon's Upload button rather than an OS file drop: both reach the same
// PrepareUploadAsync/UploadConflictResolver path, and a real drag-and-drop of an OS file is not something
// Playwright can produce faithfully — the conflict, not the gesture, is what is under test.
[Collection(UiCollection.Name)]
public class WebNameConflictTests
{
    private readonly SelfHostedAppFixture _app;

    public WebNameConflictTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Filing_over_a_taken_name_offers_a_new_version_or_a_new_name()
    {
        var page = await Ui.LoginAsync(_app);
        var stem = "conflict-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();

        // The document that will be collided with.
        await UploadAsync(page, stem + ".txt", "FIRSTCONTENT");
        await Expect(list.GetByText(stem)).ToBeVisibleAsync();

        // ---- Same name again → the dialog, answered "a new version of that document" -----------------------
        await UploadAsync(page, stem + ".txt", "SECONDCONTENT");

        var dialog = page.Locator(".mud-dialog").Last;
        await Expect(dialog).ToContainTextAsync("already exists");

        await dialog.GetByText("File it as a new version").ClickAsync();
        await FillMudAsync(dialog.Locator("textarea"), "second revision");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "File" }).ClickAsync();

        // The proof is the CONTENT, not the row count: a new version became current, so the preview shows the
        // second file. A row count alone would also pass if the upload had simply been discarded.
        await Expect(list.GetByText(stem)).ToBeVisibleAsync();
        await list.GetByText(stem).First.ClickAsync();
        await Expect(page.Locator(".wb-preview")).ToContainTextAsync("SECONDCONTENT");

        // ---- Same name a third time → the dialog, answered "a new document with a different name" ----------
        await UploadAsync(page, stem + ".txt", "THIRDCONTENT");

        var renameDialog = page.Locator(".mud-dialog").Last;
        await renameDialog.GetByText("File it as a new document").ClickAsync();
        await renameDialog.GetByRole(AriaRole.Button, new() { Name = "File" }).ClickAsync();

        // The offered free name is the stem with a counter, so the folder now holds BOTH — the original under
        // its own name and the new one beside it.
        await Expect(list.GetByText($"{stem} (2)")).ToBeVisibleAsync();
        await list.GetByText($"{stem} (2)").First.ClickAsync();
        await Expect(page.Locator(".wb-preview")).ToContainTextAsync("THIRDCONTENT");
    }

    [Fact]
    public async Task Cancelling_the_dialog_files_nothing()
    {
        var page = await Ui.LoginAsync(_app);
        var stem = "cancel-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        await UploadAsync(page, stem + ".txt", "ORIGINALCONTENT");
        await Expect(list.GetByText(stem)).ToBeVisibleAsync();

        await UploadAsync(page, stem + ".txt", "SHOULDNOTAPPEAR");
        var dialog = page.Locator(".mud-dialog").Last;
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        // Nothing was created under a second name, and the original still shows its own content — cancelling
        // must not be a quiet version-bump.
        await Expect(list.GetByText($"{stem} (2)")).Not.ToBeVisibleAsync();
        await list.GetByText(stem).First.ClickAsync();
        await Expect(page.Locator(".wb-preview")).ToContainTextAsync("ORIGINALCONTENT");
    }

    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()"); // MudTextField commits on blur (no Immediate)
    }

    private static async Task UploadAsync(IPage page, string fileName, string content)
    {
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync();
        });

        await chooser.SetFilesAsync(new FilePayload
        {
            Name = fileName,
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes(content),
        });
    }
}
