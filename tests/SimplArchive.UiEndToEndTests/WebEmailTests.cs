using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADRs 0260/0261): uploading a .eml auto-classifies it — the document is renamed to the email
// subject and its attachment is filed as a child document (reachable by drilling in).
[Collection(UiCollection.Name)]
public class WebEmailTests
{
    private readonly SelfHostedAppFixture _app;

    public WebEmailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Uploading_an_email_renames_to_the_subject_and_files_the_attachment_as_a_child()
    {
        var page = await Ui.LoginAsync(_app);
        var subject = "E2E-Subject-" + Guid.NewGuid().ToString("N")[..8];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator(".wb-ribbon").GetByText("Upload").First.ClickAsync();
        });
        await chooser.SetFilesAsync(new FilePayload { Name = "email-" + Guid.NewGuid().ToString("N")[..8] + ".eml", MimeType = "message/rfc822", Buffer = BuildEml(subject) });

        // Auto-classified: the document is named after the subject.
        await Expect(list.GetByText(subject)).ToBeVisibleAsync();

        // Drilling into the email (it has the attachment as a child) shows the attachment.
        await list.GetByText(subject).First.DblClickAsync();
        await Expect(list.GetByText("attach", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static byte[] BuildEml(string subject)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var eml =
            "From: alice@example.com\r\n" +
            "To: bob@example.com\r\n" +
            $"Subject: {subject}\r\n" +
            "Date: Mon, 01 Jan 2024 10:00:00 +0000\r\n" +
            $"Message-ID: <{messageId}@example.com>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"B\"\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Email body.\r\n" +
            "\r\n" +
            "--B\r\n" +
            "Content-Type: text/plain; name=\"attach.txt\"\r\n" +
            "Content-Disposition: attachment; filename=\"attach.txt\"\r\n" +
            "\r\n" +
            "attachment body\r\n" +
            "--B--\r\n";
        return Encoding.ASCII.GetBytes(eml);
    }
}
