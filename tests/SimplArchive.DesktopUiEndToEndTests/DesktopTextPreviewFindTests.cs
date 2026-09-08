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
}
