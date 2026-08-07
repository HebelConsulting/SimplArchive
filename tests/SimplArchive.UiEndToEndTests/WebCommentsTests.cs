using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// A UI flow (ADR 0222): the chat pane sends a message on the selected node and a threaded reply — both appear
// with the author. Chatting on the repository folder keeps it independent of the document-focused tests.
[Collection(UiCollection.Name)]
public class WebCommentsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCommentsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Posting_a_comment_and_a_reply_shows_them_in_the_thread()
    {
        var page = await Ui.LoginAsync(_app);
        var body = "e2e-comment-" + Guid.NewGuid().ToString("N")[..8];
        var reply = "e2e-reply-" + Guid.NewGuid().ToString("N")[..8];

        // Selecting a node (the repository folder) drives the chat pane.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var chat = page.Locator(".wb-chat");

        // Send a top-level message.
        await FillMudAsync(chat.Locator("textarea[placeholder*='Write a message']"), body);
        await chat.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();
        await Expect(chat.Locator(".wb-comment-body").Filter(new() { HasText = body })).ToBeVisibleAsync();

        // Reply to it.
        await chat.Locator(".wb-reply-link").First.ClickAsync();
        await FillMudAsync(chat.Locator("textarea[placeholder*='Write a reply']"), reply);
        await chat.GetByRole(AriaRole.Button, new() { Name = "Reply" }).ClickAsync();
        await Expect(chat.Locator(".wb-comment-reply").Filter(new() { HasText = reply })).ToBeVisibleAsync();
    }

    // MudTextField (no Immediate) commits its bound value on the change event, i.e. on blur — fill then blur so
    // the value reaches the model and the Send button enables.
    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()");
    }
}
