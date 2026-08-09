namespace SimplArchive.UnitTests;

// Both clients' preview toolbars must WRAP when the pane is narrow, rather than letting their controls escape the
// pane and draw over the neighbouring chat pane (issue #419) — a control outside its pane can obscure or intercept
// clicks meant for something else, which is the same class of defect as the clipped Revoke button (#410).
//
// The desktop never had the bug: its toolbar is a WrapPanel. The web's was a plain flex row and did. Fixing the web
// is worth little if either client later loses the property — and it is easy to lose invisibly, because a
// WrapPanel "tidied" into a StackPanel, or a dropped `flex-wrap`, looks identical at a comfortable width and
// changes nothing any behavioural test drives.
//
// This is a SHAPE guard, in the manner of NoBareApiExceptionTests: cheap, and it covers the client the geometry
// test cannot reach. The real measurement lives in WebPreviewToolbarOverflowTests, which narrows the pane and
// asserts every control's box stays inside it — structure here, geometry there.
public class PreviewToolbarWrapsTests
{
    [Fact]
    public void The_desktop_preview_toolbar_is_a_wrap_panel()
    {
        var axaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SimplArchive.DesktopClient", "Views", "PreviewPane.axaml"));

        Assert.True(axaml.Contains("<WrapPanel", StringComparison.Ordinal),
            "The desktop preview toolbar is no longer a WrapPanel. Its control groups would then run off the pane "
            + "at narrow widths instead of wrapping onto another line (issue #419).");
    }

    [Fact]
    public void The_web_preview_toolbar_wraps()
    {
        var home = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SimplArchive.Client", "Pages", "Home.razor"));
        var rule = home.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(".wb-pv-findbar {", StringComparison.Ordinal));

        Assert.NotNull(rule);
        Assert.True(rule!.Contains("flex-wrap: wrap", StringComparison.Ordinal),
            $"The web preview toolbar must declare flex-wrap: wrap, or its controls overflow the pane and draw over "
            + $"the chat pane (issue #419). Found:{Environment.NewLine}{rule.Trim()}");

        // Without min-width:0 a flex item refuses to shrink below its content, so the row pushes past its parent
        // before wrapping ever comes into play.
        Assert.True(rule.Contains("min-width: 0", StringComparison.Ordinal),
            $"The web preview toolbar must declare min-width: 0 so it can shrink (issue #419). Found:{Environment.NewLine}{rule.Trim()}");
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
