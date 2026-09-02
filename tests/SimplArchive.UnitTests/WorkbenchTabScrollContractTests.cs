using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A flex child that scrolls must declare `min-height: 0`, or it will not shrink and its PARENT overflows —
// carrying the workbench's bottom tab bar off-screen, so the user cannot reach another tab at all.
//
// This is a SOURCE check on purpose. WebTabBarTests already measures the bar's position on each tab, and it
// could not see this: it renders whatever the test tenant happens to hold, and with a short list the broken
// markup looks perfect. Five of the seven tabs that had the defect were already in that [Theory] and passing.
// The bug only appears once a list grows — which is how it reached a live demo, and why CLAUDE.md records that
// it "has regressed more than once".
//
// The contract is checkable without rendering anything, so it is checked in the fast tier where it costs
// nothing and cannot depend on data.
public partial class WorkbenchTabScrollContractTests
{
    [Fact]
    public void Every_scrolling_flex_child_in_a_tab_can_shrink()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoPaths.Root(), "src", "SimplArchive.Client", "Components", "Tabs"), "*.razor"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ScrollingFlexChild().Matches(text))
            {
                if (!m.Value.Contains("min-height", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A scrolling flex child must declare min-height:0, or it refuses to shrink below its content and "
            + "pushes the workbench past the viewport — taking the bottom tab bar with it, so no other tab can "
            + "be reached:\n  " + string.Join("\n  ", offenders));
    }

    // `flex:1 1 auto` … `overflow:auto` in one inline style — the shape every workbench tab uses for its
    // scrolling region. Deliberately narrow: it is the pattern that had the defect, not every possible way of
    // writing one, so a rewrite in another shape is not silently blessed by a test that claims to cover it.
    [GeneratedRegex(@"style=""[^""]*flex:\s*1\s+1\s+auto[^""]*overflow:\s*auto[^""]*""", RegexOptions.IgnoreCase)]
    private static partial Regex ScrollingFlexChild();

}
