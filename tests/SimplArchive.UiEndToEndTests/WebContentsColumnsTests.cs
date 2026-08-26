using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The contents list's columns (issue #768): Type, Doc date, Size and Tags were reported as rendering but
// carrying no data.
//
// They are collapsed BY DESIGN below a pane width of 520px (container queries, ADR "List-row columns and
// sorting"), so anything asserted at the default split is asserting the collapse, not the data. The pane is
// widened here before a single cell is read.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebContentsColumnsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebContentsColumnsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_wide_list_pane_shows_the_type_date_size_and_tag_of_a_document()
    {
        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        await tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = "Demo Repository" }).First.ClickAsync();
        await Expect(list.Locator(".wb-list-row").First).ToBeVisibleAsync();

        // Into a folder that holds DOCUMENTS: the repository root of this fixture holds only folders, and a
        // folder legitimately has no size and the literal type "Folder", so it cannot answer the question.
        for (var depth = 0; depth < 4; depth++)
        {
            var documentRow = list.Locator(".wb-list-row").Filter(new() { HasNot = page.Locator(".wb-glyph-folder, .wb-glyph-empty") });
            if (await documentRow.CountAsync() > 0)
            {
                break;
            }

            await list.Locator(".wb-list-row").First.DblClickAsync();
            await page.WaitForTimeoutAsync(700);
        }

        // The pane is its own container query root, so widening IT is what brings the columns back — a wider
        // viewport alone would not.
        await page.EvaluateAsync(@"() => {
            const pane = document.querySelector(""[data-pane='list']"");
            pane.style.flex = '0 0 1000px';
            pane.style.maxWidth = 'none';
            pane.style.minWidth = '1000px';
        }");
        await page.WaitForTimeoutAsync(500);

        var row = list.Locator(".wb-list-row").Filter(new() { HasNot = page.Locator(".wb-glyph-folder, .wb-glyph-empty") }).First;
        await Expect(row).ToBeVisibleAsync();

        var cells = await row.Locator(".wb-ccell:not(.wb-cname)").AllTextContentsAsync();
        var name = await row.Locator(".wb-cname").First.TextContentAsync();
        Assert.True(cells.Count >= 3, $"expected the data cells, got {cells.Count}: {string.Join(" | ", cells)}");

        // Asserted individually so a failure names WHICH column is blank rather than leaving the next reader
        // to work it out. Tags is deliberately not asserted: a document legitimately has none.
        Assert.False(string.IsNullOrWhiteSpace(cells[0]), $"Type is blank for '{name}': {string.Join(" | ", cells)}");
        Assert.False(string.IsNullOrWhiteSpace(cells[1]), $"Doc date is blank for '{name}': {string.Join(" | ", cells)}");
        Assert.False(string.IsNullOrWhiteSpace(cells[2]), $"Size is blank for '{name}': {string.Join(" | ", cells)}");

        // The owner column (#768) was absent entirely rather than blank — it is the LAST data cell, and it is
        // the first to collapse when the pane narrows, hence the generous width above.
        Assert.True(cells.Count >= 5, $"the owner column is missing; cells: {string.Join(" | ", cells)}");
        Assert.False(string.IsNullOrWhiteSpace(cells[4]), $"Owner is blank for '{name}': {string.Join(" | ", cells)}");
    }
}
