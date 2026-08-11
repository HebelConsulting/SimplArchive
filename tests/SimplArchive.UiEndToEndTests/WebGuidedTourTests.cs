using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace SimplArchive.UiEndToEndTests;

// The guided tour (issue #414) is published for a visitor's own agent to perform against the live demo. That
// makes it the one artefact whose failures happen somewhere we will never see: on a stranger's machine, in a
// video nobody sends us. So the anchors it names are checked here against the real app.
//
// The tour is a CONTRACT, in the same sense as a rel name (ADR 0543): the DOM around an anchor may be
// reorganised freely, the anchor may not be renamed. This test is what makes that promise real rather than
// stated — rename `data-tour="pane-list"` and it fails here, not in a stranger's recording.
//
// It parses the anchors out of the published tour rather than restating them, so the two cannot drift: a step
// added to the document is automatically covered, and one whose anchor is misspelled fails immediately.
//
// Deliberately NOT asserted: the narration, and anything about visible text. The UI is translated into four
// languages, so a text assertion would be valid in exactly one of them — which is the audience the tour is least
// aimed at. Anchors and `data-tour-*` values are language-independent, which is why the tour was written to
// assert only those.
[Collection(UiCollection.Name)]
public partial class WebGuidedTourTests
{
    private readonly SelfHostedAppFixture _app;

    public WebGuidedTourTests(SelfHostedAppFixture app) => _app = app;

    // The tour names anchors as bare, surface-neutral names in backticks (`pane-list`), because one step now
    // serves the web AND the desktop client — the browser looks the name up as `data-tour`, the desktop as an
    // accessibility automation id (issue #414). Scanning the prose for `data-tour="…"` therefore no longer
    // finds them; scan the step blocks instead. The desktop guard parses the SAME file with the same shape.
    [GeneratedRegex(@"^(?:anchor|action|expect):.*$", RegexOptions.Multiline)]
    private static partial Regex StepLine();

    [GeneratedRegex(@"`(?<anchor>(?:pane|tab|action)-[a-z0-9-]+)`")]
    private static partial Regex TourAnchor();

    /// <summary>Every anchor named anywhere in the tour's step blocks, deduplicated.</summary>
    internal static List<string> AnchorsNamedInTheTour(string tourMarkdown) =>
        StepLine().Matches(tourMarkdown)
            .SelectMany(line => TourAnchor().Matches(line.Value).Select(m => m.Groups["anchor"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task Every_anchor_the_tour_names_exists_in_the_app()
    {
        var tourPath = Path.Combine(RepoRoot(), "src", "SimplArchive.Client", "wwwroot", "tour", "tour.md");
        Assert.True(File.Exists(tourPath), $"The published tour is missing: {tourPath}");

        var anchors = AnchorsNamedInTheTour(File.ReadAllText(tourPath));

        // Anti-vacuous, sample-independent: if the parse breaks, the loop below would assert nothing at all.
        Assert.True(anchors.Count >= 6, $"parsed only {anchors.Count} anchors from the tour — the scan is broken");

        var page = await Ui.LoginAsync(_app);

        // Step 3 of the tour selects a folder before the list has rows; do the same, so the anchors that only
        // exist once something is selected are genuinely present when checked.
        await page.GetByText("Demo Repository").First.ClickAsync();
        var firstRow = page.Locator("[data-tour='pane-list'] .wb-list-row").First;
        await firstRow.WaitForAsync(new() { Timeout = 30000 });
        var rowName = await firstRow.Locator(".wb-cname").First.GetAttributeAsync("title");
        await firstRow.ClickAsync();

        // Wait until the pane actually DESCRIBES the row that was clicked. Several anchors sit on actions gated
        // by the selected subject's rights, and those arrive with its detail load (ADR 0559) — checking before it
        // lands used to succeed only because the pane was still showing the PREVIOUS subject's affordances, which
        // is the defect ADR 0559 removed. Without this wait the test asserts against a half-loaded pane and
        // reports `action-manage-access` missing.
        await page.Locator("[data-pane='index']").GetByText(rowName!).First.WaitForAsync(new() { Timeout = 30000 });

        var missing = new List<string>();
        foreach (var anchor in anchors)
        {
            if (await page.Locator($"[data-tour='{anchor}']").CountAsync() == 0)
            {
                missing.Add(anchor);
            }
        }

        // Some anchors only exist in a STATE the tour puts the app into — `action-save-index` appears when
        // editing begins and is gone once saved, which is exactly what its step asserts. Checking only the
        // resting state would report those as missing and push someone to delete a step that works. So enter
        // edit mode and re-check what the first pass did not find (issue #414, full track).
        if (missing.Count > 0 && await page.Locator("[data-tour='action-edit-index']").CountAsync() > 0)
        {
            await page.Locator("[data-tour='action-edit-index']").ClickAsync();

            // Blazor re-renders the pane before the edit-only controls exist; checking instantly would report
            // them missing and blame the tour for a race in the test.
            foreach (var anchor in missing)
            {
                try
                {
                    await page.Locator($"[data-tour='{anchor}']").First.WaitForAsync(new() { Timeout = 5000 });
                }
                catch (TimeoutException)
                {
                    // Genuinely absent — the re-check below records it.
                }
            }

            var stillMissing = new List<string>();
            foreach (var anchor in missing)
            {
                if (await page.Locator($"[data-tour='{anchor}']").CountAsync() == 0)
                {
                    stillMissing.Add(anchor);
                }
            }

            // Leave the app as it was found — this fixture is shared with every other UI test.
            var cancel = page.GetByRole(AriaRole.Button, new() { Name = "Cancel" });
            if (await cancel.CountAsync() > 0)
            {
                await cancel.First.ClickAsync();
            }

            missing = stillMissing;
        }

        Assert.True(missing.Count == 0,
            "The published guided tour names anchors that no longer exist in the app, so a visitor's agent would "
            + "follow it into nothing (issue #414):\n  " + string.Join("\n  ", missing)
            + "\n\nEither restore the anchor or update wwwroot/tour/tour.md — the anchor names are the contract, "
            + "not the surrounding markup.");
    }

    // The tour's assertions are machine-readable so they hold in any language; this checks the values it reads
    // are actually emitted, since an absent attribute reads the same as a wrong one to an agent.
    [Fact]
    public async Task The_values_the_tour_asserts_on_are_emitted()
    {
        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-tour='pane-list'] .wb-list-row").First.WaitForAsync(new() { Timeout = 30000 });

        var roots = await page.Locator("[data-tour='pane-tree']").GetAttributeAsync("data-tour-roots");
        Assert.True(int.TryParse(roots, out var rootCount) && rootCount >= 1,
            $"pane-tree must publish a numeric data-tour-roots; got '{roots}'");

        var rows = await page.Locator("[data-tour='pane-list']").GetAttributeAsync("data-tour-rows");
        Assert.True(int.TryParse(rows, out var rowCount) && rowCount >= 1,
            $"pane-list must publish a numeric data-tour-rows; got '{rows}'");

        // A tab publishes whether it is the open one, so "the audit tab is now open" is assertable without
        // reading a translated label.
        await page.Locator("[data-tour='tab-audit']").ClickAsync();
        Assert.Equal("true", await page.Locator("[data-tour='tab-audit']").GetAttributeAsync("data-tour-active"));
        Assert.Equal("false", await page.Locator("[data-tour='tab-repositories']").GetAttributeAsync("data-tour-active"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
