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
}
