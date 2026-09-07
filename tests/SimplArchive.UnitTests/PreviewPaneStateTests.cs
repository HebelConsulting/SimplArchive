using SimplArchive.Client.Pages;

namespace SimplArchive.UnitTests;

// A deterministic guard on PreviewPaneState.Clear() (ADR 0294), which PreviewPane.ClearAsync() delegates to.
//
// Written when the state was SHARED by the Repositories and Intray tabs, where a missed field meant one tab's
// preview leaking into the other's host. Each tab now owns a PreviewPane and therefore its own state (ADR
// 0558), so that particular leak is gone — but the reset still has to be complete, because a pane is cleared
// whenever its selection goes away and a half-reset one would render a stale document's text or find results
// against the next selection.
public class PreviewPaneStateTests
{
    [Fact]
    public void Clear_resets_the_content_state_so_no_stale_preview_can_render()
    {
        var state = new PreviewPaneState
        {
            Kind = "pdf",
            Text = "stale body from the other tab",
            FindQuery = "term",
            Count = 5,
            Index = 3,
            Converted = true,
        };
        Assert.True(state.HasPages);

        state.Clear();

        Assert.Equal("", state.Kind);
        Assert.False(state.HasPages);
        Assert.Null(state.Text);
        Assert.Equal("", state.FindQuery);
        Assert.Equal(0, state.Count);
        Assert.Equal(0, state.Index);
        Assert.False(state.Converted);
    }

    [Theory]
    [InlineData("image", true)]
    [InlineData("pdf", true)]
    [InlineData("text", false)]
    [InlineData("unsupported", false)]
    [InlineData("", false)]
    public void HasPages_is_true_only_for_page_rendered_kinds(string kind, bool expected) =>
        Assert.Equal(expected, new PreviewPaneState { Kind = kind }.HasPages);

    // ---- Find in a text preview (#1063) — the pure half the pane renders from ----

    private static PreviewPaneState TextState(string text) => new() { Kind = "text", Text = text };

    [Fact]
    public void ApplyTextFind_counts_case_insensitively_and_activates_the_first_match()
    {
        var state = TextState("LSPG one lspg two LSPG");
        state.FindQuery = "lspg";
        state.ApplyTextFind();

        Assert.Equal(3, state.Count);
        Assert.Equal(1, state.Index);
        Assert.Equal([0, 9, 18], state.TextMatches);
    }

    [Fact]
    public void CycleTextFind_wraps_both_directions()
    {
        var state = TextState("a b a");
        state.FindQuery = "a";
        state.ApplyTextFind();

        state.CycleTextFind(+1);
        Assert.Equal(2, state.Index);
        state.CycleTextFind(+1);
        Assert.Equal(1, state.Index); // wrapped forward
        state.CycleTextFind(-1);
        Assert.Equal(2, state.Index); // wrapped back
    }

    [Fact]
    public void TextSegments_reassemble_to_the_text_and_mark_only_the_active_hit()
    {
        var state = TextState("x LSPG y LSPG z");
        state.FindQuery = "LSPG";
        state.ApplyTextFind();
        state.CycleTextFind(+1); // second match active

        var segments = state.TextSegments().ToList();
        Assert.Equal("x LSPG y LSPG z", string.Concat(segments.Select(s => s.Segment)));
        Assert.Equal(2, segments.Count(s => s.IsHit));
        Assert.Single(segments, s => s.IsActive);
        Assert.Equal("LSPG", segments.Last(s => s.IsHit).Segment);
        Assert.True(segments.Last(s => s.IsHit).IsActive);
    }

    [Fact]
    public void ApplyTextFind_caps_the_matches_against_degenerate_queries()
    {
        var state = TextState(new string('a', PreviewPaneState.MaxTextMatches * 3));
        state.FindQuery = "a";
        state.ApplyTextFind();

        Assert.Equal(PreviewPaneState.MaxTextMatches, state.Count);
    }

    [Fact]
    public void An_empty_query_or_a_paged_kind_clears_the_matches()
    {
        var state = TextState("LSPG");
        state.FindQuery = "LSPG";
        state.ApplyTextFind();
        Assert.Equal(1, state.Count);

        state.FindQuery = string.Empty;
        state.ApplyTextFind();
        Assert.Equal(0, state.Count);
        Assert.Empty(state.TextMatches);

        state.Kind = "pdf";
        state.FindQuery = "LSPG";
        state.ApplyTextFind();
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Clear_drops_the_text_matches_too()
    {
        var state = TextState("LSPG");
        state.FindQuery = "LSPG";
        state.ApplyTextFind();

        state.Clear();

        Assert.Empty(state.TextMatches);
        Assert.Equal(0, state.Count);
    }
}
