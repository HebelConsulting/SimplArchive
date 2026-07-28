using SimplArchive.Client.Pages;

namespace SimplArchive.UnitTests;

// Task 3 (deterministic guard): the shared preview state (used by both the Repositories and Inbox tabs, ADR
// 0294) must fully reset on a tab switch so one tab's preview can't leak into the other's shared host. This
// tests the extracted PreviewPaneState.Clear() that Home.ClearPreviewPane() delegates to.
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
