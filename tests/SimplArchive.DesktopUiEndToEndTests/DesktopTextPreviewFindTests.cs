using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Find in a TEXT preview (#1063's desktop half — the web's pattern promoted per ADR 0511): the find bar the
// pages already have now works when the preview is text. Pure VM: the matches come from the shared
// SimplArchive.Presentation.TextFind (whose arithmetic is pinned in the unit tests); these pin the view
// model's routing — count/position, wrap, the active segment, and that the surface clears with the preview.
public class DesktopTextPreviewFindTests
{
    private static PreviewViewModel TextPreview(string text)
    {
        var vm = new PreviewViewModel(new TestShell()) { PreviewText = text };
        return vm;
    }

    [Fact]
    public void Typing_a_query_over_a_text_preview_counts_and_positions()
    {
        var vm = TextPreview("LSPG Kaegiswil\nLSZC Buochs\nLSPG again");

        vm.FindQuery = "lspg";

        Assert.Equal(2, vm.FindCount);
        Assert.Equal("1 / 2", vm.FindPosition);
        Assert.True(vm.CanFindNavigate);
        Assert.Equal(0, vm.ActiveTextMatchOffset);
    }

    [Fact]
    public void Next_advances_and_wraps_and_the_active_segment_follows()
    {
        var vm = TextPreview("a b a");
        vm.FindQuery = "a";

        vm.FindNextCommand.Execute(null);
        Assert.Equal("2 / 2", vm.FindPosition);
        Assert.Equal(4, vm.ActiveTextMatchOffset);
        Assert.Single(vm.TextFindSegments(), s => s is { IsHit: true, IsActive: true, Segment: "a" });

        vm.FindNextCommand.Execute(null);
        Assert.Equal("1 / 2", vm.FindPosition); // wrapped, like a browser find
        Assert.Equal(0, vm.ActiveTextMatchOffset);
    }

    [Fact]
    public void A_query_with_no_hits_reads_zero_and_disables_navigation()
    {
        var vm = TextPreview("nothing here");

        vm.FindQuery = "LSZH";

        Assert.Equal("0 / 0", vm.FindPosition);
        Assert.False(vm.CanFindNavigate);
        Assert.Equal(-1, vm.ActiveTextMatchOffset);
    }

    [Fact]
    public void Reset_clears_the_find_surface_but_keeps_the_query()
    {
        // The query deliberately survives a document switch (it reapplies to the next preview, like the
        // pages' search-seeded find); the MATCHES and the bar's gate must not — an unsupported preview
        // after a text one otherwise keeps an inert find bar (ADR 0550's affordance rule).
        var vm = TextPreview("LSZH");
        vm.CanFindInDocument = true;
        vm.FindQuery = "LSZH";
        Assert.Equal(1, vm.FindCount);

        vm.Reset("Preview not supported.");

        Assert.False(vm.CanFindInDocument);
        Assert.Equal(0, vm.FindCount);
        Assert.Equal("LSZH", vm.FindQuery);
        Assert.Equal(-1, vm.ActiveTextMatchOffset);
    }

    [Fact]
    public void Wrap_defaults_on_toggles_and_resets_per_document()
    {
        // #1317: ON by default (nothing is silently clipped for a user who never finds the toggle), and
        // EPHEMERAL — a new document resets it, the fullscreen toggle's lifetime (ADRs 0295/0297).
        var vm = TextPreview("one\ntwo");
        Assert.True(vm.PreviewWrap);
        Assert.Equal("mdi-wrap", vm.PreviewWrapIcon);

        vm.TogglePreviewWrapCommand.Execute(null);
        Assert.False(vm.PreviewWrap);
        Assert.Equal("mdi-wrap-disabled", vm.PreviewWrapIcon);

        vm.PreviewText = "another document";
        Assert.True(vm.PreviewWrap);
    }

    [Fact]
    public void A_shorter_text_never_meets_the_previous_texts_matches()
    {
        // Regression (#1317, found live by the wrap hook): the matches belonged to the PREVIOUS text, the
        // view renders on the PreviewText change, and a stale offset cut out of a shorter text is a
        // Substring crash. The recompute must happen before the view can observe the new text.
        var vm = TextPreview(string.Join('\n', Enumerable.Range(0, 50).Select(i => $"line {i} LSPG")));
        vm.FindQuery = "lspg";
        Assert.Equal(50, vm.FindCount);

        vm.PreviewText = "short";

        Assert.Equal(0, vm.FindCount);
        Assert.Equal(-1, vm.ActiveTextMatchOffset);
        Assert.All(vm.TextFindSegments(), s => Assert.False(s.IsHit)); // enumerating must not throw
    }
}
