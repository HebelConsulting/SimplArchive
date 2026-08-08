using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The cross-document links dialog's row actions (issue #410). The row gained Go to and Show without the dialog
// growing, and Revoke — the only destructive one, and the one somebody reaches for after a link has leaked — was
// pushed past the dialog's edge.
//
// The assertion is VISIBILITY, not presence. The existing tests drive behaviour, which is exactly why a
// clipped-but-working button passed them: it was in the DOM, it was bound, it simply could not be seen.
[Collection(UiCollection.Name)]
public class WebExternalLinkRowActionsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebExternalLinkRowActionsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task All_three_row_actions_are_visible_in_the_links_dialog()
    {
        var page = await Ui.LoginAsync(_app);

        // The ribbon entry exists only when the API root advertises the rel, which the demo tenant does.
        await page.GetByRole(AriaRole.Button, new() { Name = "My external links" }).First.ClickAsync();

        var row = page.Locator("tbody tr").First;
        await Expect(row).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Each action in turn — visible, not merely present.
        foreach (var label in new[] { "Go to", "Show", "Revoke" })
        {
            await Expect(row.GetByRole(AriaRole.Button, new() { Name = label })).ToBeVisibleAsync();
        }

        // And inside the dialog's box: an element can report "visible" while sitting past the edge of a clipping
        // ancestor, which is the exact failure being guarded against.
        var dialog = page.Locator(".mud-dialog").First;
        var dialogBox = await dialog.BoundingBoxAsync();
        var revokeBox = await row.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).BoundingBoxAsync();

        Assert.NotNull(dialogBox);
        Assert.NotNull(revokeBox);
        Assert.True(revokeBox!.X + revokeBox.Width <= dialogBox!.X + dialogBox.Width + 1,
            $"Revoke ends at {revokeBox.X + revokeBox.Width:F0}px, past the dialog's right edge at "
            + $"{dialogBox.X + dialogBox.Width:F0}px — it is clipped.");
    }
}
