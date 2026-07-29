using System.IO.Compression;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0263): a .zip is filed as-is and, on double-click, its entries are browsed virtually (nothing
// unpacked) with a back affordance.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebZipTests
{
    private readonly SelfHostedAppFixture _app;

    public WebZipTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Zip_entries_are_browsed_virtually()
    {
        var page = await Ui.LoginAsync(_app);
        var name = "archive-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        // Upload the zip into the repository.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = name + ".zip", MimeType = "application/zip", Buffer = MakeZip() });
        await Expect(list.GetByText(name)).ToBeVisibleAsync();

        // Double-click → browse the entries virtually, with a back affordance.
        await list.GetByText(name).First.DblClickAsync();
        await Expect(list.GetByText("hello.txt")).ToBeVisibleAsync();
        await Expect(list.GetByText("docs/world.txt")).ToBeVisibleAsync();
        await Expect(list.GetByText("— back")).ToBeVisibleAsync();

        // Back exits the archive view.
        await list.GetByText("— back").First.ClickAsync();
        await Expect(list.GetByText("hello.txt")).Not.ToBeVisibleAsync();
    }

    private static byte[] MakeZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("hello.txt").Open()))
            {
                w.Write("hello world");
            }

            using (var w = new StreamWriter(zip.CreateEntry("docs/world.txt").Open()))
            {
                w.Write("nested");
            }
        }

        return ms.ToArray();
    }
}
