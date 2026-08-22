using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Two defects found by looking at the built thing, both invisible to an assertion that only asked whether
// something EXISTED.
//
//  1. The selected-node ring (#686) marked a whole SUBTREE. The class is applied to exactly one node, so
//     counting it proves nothing — the bug was a CSS combinator, and only the computed outline shows it.
//  2. A document wearing a mask the catalogue does not offer (#671) rendered its mask as a bare GUID, beside a
//     dropdown whose only offers were the wrong ones.
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

        // Marked by selecting the folder in the CONTENTS LIST, which is the feature (#686) and also leaves the
        // tree node unfocused — clicking the node itself paints a focus outline that would satisfy the check
        // below without the rule under test contributing anything.
        await list.Locator(".wb-list-row").Filter(new() { HasText = "My Documents" }).First.ClickAsync();

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
    public async Task A_mask_the_catalogue_does_not_offer_is_shown_by_name_and_cannot_be_changed()
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
        await Expect(maskRow.Locator("input")).ToHaveValueAsync("Calendar");

        // ...and nothing else is on offer, because every alternative is a refusal the containment invariant
        // would deliver after the save instead of before it.
        await Expect(maskRow.Locator(".mud-select")).ToHaveCountAsync(0);
    }
}
