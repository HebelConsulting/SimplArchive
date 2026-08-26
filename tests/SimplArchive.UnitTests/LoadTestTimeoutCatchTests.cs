using Microsoft.Playwright;

namespace SimplArchive.UnitTests;

// Which exception a Playwright timeout throws is NOT deducible from the type it does not define, and getting
// it wrong disables a diagnostic block silently (#754).
//
// The tempting reasoning: Microsoft.Playwright publishes no TimeoutException, so `catch (TimeoutException)`
// binds to System's, so it can never fire. The first two steps are true and the conclusion is FALSE —
// Playwright .NET throws System.TimeoutException on purpose. Following that reasoning and "correcting" the
// catch to PlaywrightException disabled the failure diagnostics for a full 18-minute run, which reported the
// same bare "Timeout 120000ms exceeded" it was written to replace, and looked careful while doing it.
//
// Probed against a real browser: a wait on a missing locator, a click on one, and a Filter matching nothing
// ALL threw System.TimeoutException.
public class LoadTestTimeoutCatchTests
{
    [Fact]
    public void Playwright_publishes_no_TimeoutException_which_is_why_Systems_is_the_right_one()
    {
        // The premise, asserted rather than remembered — but note what it does NOT license. If a future
        // Playwright starts publishing its own, this fails, and the next reader must re-probe what is actually
        // thrown instead of inferring it from the type list either way.
        Assert.DoesNotContain(typeof(IPage).Assembly.GetTypes(), t => t.IsPublic && t.Name == "TimeoutException");
    }

    [Fact]
    public void The_browser_workload_catches_the_type_playwright_actually_throws()
    {
        // Comment lines are stripped: the comment in BrowserUser that explains this trap necessarily contains
        // the phrases below, and a guard that cannot tell code from the prose describing it would force the
        // explanation to be deleted to stay green.
        var source = string.Join(
            '\n',
            File.ReadAllLines(Path.Combine(RepoRoot(), "tests", "SimplArchive.LoadTest", "BrowserUser.cs"))
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains("catch (TimeoutException", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (PlaywrightException",
            source,
            StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx).");
    }
}
