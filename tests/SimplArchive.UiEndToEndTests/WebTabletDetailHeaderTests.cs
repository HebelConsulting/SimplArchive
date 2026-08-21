using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The detail pane stacks on a landscape tablet (#698).
//
// The symptom was reported as a cramped header — the document title wrapping to three lines in a narrow
// column. The cause is one level down and makes the whole pane wrong: the tablet-landscape rule sets
// `.wb-detail .wb-index { display: flex }` to override the phone tier's `display: none`, and never says
// `flex-direction: column`. Flex defaults to ROW, so the header, the field rows and the mask line stop being
// stacked blocks and become side-by-side columns. The title is squeezed because it is sharing the pane's width
// with the fields, not because the header is narrow.
//
// So this measures the LAYOUT rather than the title: the header must span the pane, and the fields must sit
// BELOW it rather than beside it. Asserting on the wrapped title alone would pass the moment somebody widened
// the title column, leaving the pane still in three columns.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebTabletDetailHeaderTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTabletDetailHeaderTests(SelfHostedAppFixture app) => _app = app;

    // 1366x1024 with a coarse pointer: the landscape-tablet tier the manual screenshots (ADR 0659, #684).
    // The tier keys on `(pointer: coarse) and (min-width: 768px) and (orientation: landscape)`, so all three
    // have to be true — a plain narrow desktop viewport does not enter it, which is why this is invisible
    // anywhere else.
    [Fact]
    public async Task The_detail_pane_stacks_its_header_above_its_fields_on_a_landscape_tablet()
    {
        var page = await Ui.LoginAsync(_app, configureContext: o =>
        {
            o.HasTouch = true;
            o.IsMobile = false;
            o.ViewportSize = new ViewportSize { Width = 1366, Height = 1024 };
        });

        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        foreach (var folder in new[] { "Contracts", "Acme Corp" })
        {
            await list.Locator(".wb-list-row").Filter(new() { HasText = folder }).First.DblClickAsync();
        }

        await list.Locator(".wb-list-row").Filter(new() { HasText = "Offer 2026-014" }).First.ClickAsync();

        var head = page.Locator(".wb-detail-head");
        await Expect(head).ToBeVisibleAsync();

        // Wait for the pane to hold the SUBJECT, not merely to exist: measuring (or capturing) at first paint
        // catches an empty header over a "Mask: none" pane, which looks like a different bug entirely.
        await Expect(head).ToContainTextAsync("Offer 2026-014");
        await page.WaitForTimeoutAsync(600); // the tree drawer transitions in; let it settle before measuring

        // Returned as an ARRAY: a Dictionary<string,double> round-trip through Playwright's JSON did not
        // preserve the keys, and the resulting KeyNotFoundException says nothing about the layout.
        // [paneWidth, headWidth, headBottom, rowTop, paneClientHeight, paneScrollHeight]
        var g = await page.EvaluateAsync<double[]>(@"() => {
            const pane = document.querySelector('.wb-pane.wb-index');
            const head = pane.querySelector('.wb-detail-head');
            const row  = pane.querySelector('.wb-mask-row');
            const p = pane.getBoundingClientRect(), h = head.getBoundingClientRect();
            const r = row ? row.getBoundingClientRect() : { top: h.bottom + 1 };
            return [p.width, h.width, h.bottom, r.top, pane.clientHeight, pane.scrollHeight];
        }");

        // The header spans the pane rather than sharing its width with the fields. 80% leaves room for the
        // pane's own padding without leaving room for a second column.
        Assert.True(g[1] >= g[0] * 0.8,
            $"the detail header is {g[1]:F0}px inside a {g[0]:F0}px pane — it is sharing the width with the "
            + "fields instead of spanning it");

        // ...and the fields are BELOW it, not beside it. This is the assertion that would catch a fix which
        // merely widened the title while leaving the pane in row direction.
        Assert.True(g[3] >= g[2] - 1,
            $"the first field row starts at y={g[3]:F0}, above the header's bottom edge ({g[2]:F0}) — the pane "
            + "is still laying its children out in a row");

        // ...and nothing is hidden. Stacking the pane is only half the fix: once the fields are in one column
        // they are TALLER than the desktop's INDEX_CAP of 210, so an ordinary Basic Entry (282px of content)
        // lost Retention and Mask entirely and had Current version sliced. The cap is raised for this tier
        // alone — a landscape tablet puts the pane above the preview in a 1024px-tall viewport, so the desktop's
        // trade does not carry over (ADR 0550 / #698).
        //
        // Asserting on the OVERFLOW rather than on a height: a number would pin the cap, and the thing that
        // must stay true is that this document fits, whatever the cap becomes.
        Assert.True(g[5] <= g[4] + 1,
            $"the index pane holds {g[5]:F0}px of content in {g[4]:F0}px — {g[5] - g[4]:F0}px of fields are "
            + "hidden behind a scrollbar the user has no reason to look for (ADR 0550)");
    }
}
