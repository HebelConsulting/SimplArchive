using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// Opening a folder brings it into view (#692, retriggered by ADR 0703 — moving reveals, selecting does not).
//
// This builds a DEEP chain on purpose. The issue's own reason for deferring the behaviour was that the demo
// data is two or three levels down, so the expanded branch still fits and the bad case cannot be produced —
// a test on demo data would pass whether or not anything scrolled, which is the definition of a vacuous guard.
// Fifteen levels puts the target well below the fold of a normal tree pane.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-4")]
public class WebTreeRevealScrollTests
{
    private readonly SelfHostedAppFixture _app;

    public WebTreeRevealScrollTests(SelfHostedAppFixture app) => _app = app;

    private const int Depth = 15;

    [Fact]
    public async Task A_revealed_folder_below_the_fold_is_scrolled_into_view()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        // A chain deep enough that expanding it overflows the pane, plus a sibling at the bottom to select.
        var parent = repoId;
        var prefix = $"d{Guid.NewGuid():N}"[..6];
        for (var i = 0; i < Depth; i++)
        {
            parent = (await (await http.PostAsJsonAsync($"/api/documents/{parent}/children", new { name = $"{prefix}-{i:00}" }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        var leafName = $"{prefix}-leaf";
        await http.PostAsJsonAsync($"/api/documents/{parent}/children", new { name = leafName });

        var page = await Ui.LoginAsync(_app);
        var tree = page.Locator("[data-pane='tree']");
        var list = page.Locator("[data-pane='list']");

        // Walk down by OPENING each level. Moving is what reveals now (ADR 0703): the ring marks the folder
        // being stood in, so by the last double-click it sits fifteen levels down, well below the fold. This
        // test used to end by SELECTING the leaf and expecting the ring to follow it — the behaviour #696
        // shipped and 0703 replaced. The scroll it guards is unchanged; only what triggers it moved.
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(list.Locator(".wb-list-row").First).ToBeVisibleAsync();
        for (var i = 0; i < Depth; i++)
        {
            await list.Locator(".wb-list-row").Filter(new() { HasText = $"{prefix}-{i:00}" }).First.DblClickAsync();
            await page.WaitForTimeoutAsync(500);
        }

        // The deepest folder opened — and the leaf it contains proves we are standing IN it rather than beside it.
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = leafName })).ToHaveCountAsync(1);

        var marked = tree.Locator(".wb-tree-current");
        await Expect(marked).ToHaveCountAsync(1);
        await Expect(marked).ToContainTextAsync($"{prefix}-{Depth - 1:00}");

        // The point of the issue: marked is not the same as SEEN. Compare the node's box against the pane's,
        // because "visible" in Playwright's sense is true for an element scrolled out of its own container.
        await page.WaitForTimeoutAsync(1200); // the scroll is smooth, so it has to land before measuring
        var inView = await page.EvaluateAsync<bool>(@"() => {
            const pane = document.querySelector('[data-pane=""tree""]');
            const node = pane.querySelector('.wb-tree-current');
            const p = pane.getBoundingClientRect(), n = node.getBoundingClientRect();
            return n.top >= p.top - 1 && n.bottom <= p.bottom + 1;
        }");

        Assert.True(inView, "the revealed node is marked but off-screen — which reads to a user as nothing having happened");
    }
}
