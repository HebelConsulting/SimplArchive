using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Two defects found by looking at the built thing, both invisible to an assertion that only asked whether
// something EXISTED.
//
//  1. The open-folder ring (#686) marked a whole SUBTREE. The class is applied to exactly one node, so
//     counting it proves nothing — the bug was a CSS combinator, and only the computed outline shows it.
//  2. A document wearing a mask the catalogue does not carry (#671) rendered its mask as a bare GUID, because
//     MudSelect falls back to its raw value when the value is absent from the items.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebTreeMarkerAndFixedMaskTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTreeMarkerAndFixedMaskTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_ring_marks_one_node_and_not_its_subtree()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");

        // The personal space, which always has provisioned children — the marked node needs a subtree for the
        // question to mean anything at all.
        var list = page.Locator("[data-pane='list']");

        await tree.Locator(".mud-treeview-item-content")
            .Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First.ClickAsync();

        // Marked by DRILLING INTO the folder from the contents list, which is the feature (#686 as revised: the
        // ring follows the folder you are standing in, not the row you have selected). Double-click rather than
        // click for two reasons — selecting a row deliberately no longer moves the ring at all, and drilling in
        // leaves the tree node unfocused, where clicking the node itself would paint a focus outline that
        // satisfies the check below without the rule under test contributing anything.
        await list.Locator(".wb-list-row").Filter(new() { HasText = "My Documents" }).First.DblClickAsync();

        await Expect(tree.Locator(".wb-tree-current")).ToHaveCountAsync(1);

        // The class sits on ONE wrapper either way; what differed was which rows the rule reached. So ask the
        // browser what it actually painted.
        var ringed = await page.EvaluateAsync<int>(@"() => {
            const marked = document.querySelector('.wb-tree-current');
            if (!marked) return -1;
            const own = marked.querySelector(':scope > .mud-treeview-item > .mud-treeview-item-content');
            return [...marked.querySelectorAll('.mud-treeview-item-content')]
                .filter(e => e !== own)
                .filter(e => {
                    const s = getComputedStyle(e);
                    return s.outlineStyle === 'solid' && parseFloat(s.outlineWidth) >= 2;
                }).length;
        }");

        Assert.Equal(0, ringed);

        var own = await page.EvaluateAsync<double>(@"() => {
            const marked = document.querySelector('.wb-tree-current');
            const own = marked.querySelector(':scope > .mud-treeview-item > .mud-treeview-item-content');
            const s = getComputedStyle(own);
            return s.outlineStyle === 'solid' ? parseFloat(s.outlineWidth) : 0;
        }");

        // ...and the marked row itself is still ringed, so the fix cannot be 'delete the rule'. Width is
        // asserted as a FLOOR rather than exactly 2px: the row can carry another outline of its own, and
        // pinning the number would make this a test of whichever rule happened to win.
        Assert.True(own >= 2, $"the marked row should carry a ring; measured outline width {own}px");
    }

    [Fact]
    public async Task A_mask_the_catalogue_does_not_carry_is_still_shown_by_name()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var pane = page.Locator("[data-pane='index']");

        var list = page.Locator("[data-pane='list']");

        await tree.Locator(".mud-treeview-item-content")
            .Filter(new() { HasText = SelfHostedAppFixture.AdminDisplayName }).First.ClickAsync();

        // My Calendar is a TYPED folder, so its mask is not freely assignable — the same case as the Mailbox
        // this was reported on, and unlike that one it is provisioned for every user, so the test needs no
        // mail delivery to have happened first. Reached from the contents list: the tree does not expand a
        // node merely because it was opened.
        await list.Locator(".wb-list-row").Filter(new() { HasText = "My Calendar" }).First.ClickAsync();
        await Expect(pane.GetByText("My Calendar").First).ToBeVisibleAsync();

        await pane.Locator("[data-tour='action-edit-index']").First.ClickAsync();
        await Expect(pane.Locator("[data-tour='action-save-index']")).ToBeVisibleAsync();

        // The mask reads as a NAME. A GUID here was the reported symptom: MudSelect renders its raw value when
        // the value is absent from the offered items, and the catalogue is filtered to what may be chosen.
        var maskRow = pane.Locator(".wb-mask-row").First;
        // On the ROW's rendered text, not on the select's hidden input — that one always carries the raw value
        // (it is the form field), so asserting there reads a GUID whether the bug is present or not.
        await Expect(maskRow).ToContainTextAsync("Calendar");

        // ...and it is still a PICKER. Replacing it with a read-only field was the first attempt and froze
        // every folder and extension-claimed document — a rule nobody asked for, since re-typing a Calendar
        // costs only CalDAV subscribability.
        await maskRow.Locator(".mud-input-control").First.ClickAsync();
        await Expect(page.Locator(".mud-list-item").Filter(new() { HasText = "Basic Entry" }).First).ToBeVisibleAsync();
    }
}
