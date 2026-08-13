namespace SimplArchive.UnitTests;

// The 1000-line standing-debt list, as a guard (issue #466). CLAUDE.md's principle is that no hand-written
// class exceeds 1000 lines and that over-limit files predating the rule are DEBT, not licence — but a debt
// list nobody enforces regresses invisibly: Home.razor reached 9,479 twenty lines at a time, with no single
// commit that looked unreasonable. So each over-limit file gets a CEILING at its measured size, failing on
// growth; a burn-down tranche lowers its entry in the same commit, and a file that reaches 1000 gets its
// entry DELETED — the general principle covers it from there.
//
// Home.razor is deliberately absent: it already has its own richer guard (WorkbenchShellSizeTests, with the
// slack test this one omits). One guard per file — two guards on one file WILL disagree eventually.
//
// DocumentsController is gone from this list because its split landed with the same PR that added the guard
// (2,613 → 838, five sibling controllers + DocumentAccessService + DocumentTreeQueries) — which is the shape
// every entry below wants to follow: measure, split by responsibility, then delete the entry.
public class OverLimitFileCeilingTests
{
    // File → its ceiling, measured when the guard was added (or last lowered). Growth fails; a deliberate
    // tranche lowers the number in the same commit. When a file passes under 1000, delete its entry.
    private static readonly Dictionary<string, int> Ceilings = new()
    {
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.cs"] = 6_990,
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 4_516,
        ["src/SimplArchive.DesktopClient/Views/MainWindow.axaml.cs"] = 1_945,
        ["src/SimplArchive.Api/WebDav/WebDavMiddleware.cs"] = 1_762,
        ["src/SimplArchive.DesktopClient/Views/HighlightOverlay.cs"] = 1_227,
    };

    public static TheoryData<string> Files => [.. Ceilings.Keys];

    [Theory]
    [MemberData(nameof(Files))]
    public void An_over_limit_file_does_not_grow(string file)
    {
        var path = Path.Combine(RepoRoot(), file.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{file} not found — if it moved or was deleted, update its Ceilings entry.");

        var lines = File.ReadAllLines(path).Length;
        var ceiling = Ceilings[file];

        Assert.True(lines <= ceiling,
            $"{file} is {lines} lines, over its {ceiling}-line ceiling by {lines - ceiling}.\n"
            + "This file is on the 1000-line standing-debt list (issue #466), so it may only get smaller. Put "
            + "the new code in a class that owns the responsibility — or, if it genuinely belongs here, raise "
            + "the ceiling deliberately in this same commit and say why in its message.");

        // A ceiling more than a tranche (150 lines) above reality is a stale ceiling asserting nothing —
        // bank the shrink by lowering the entry.
        Assert.True(ceiling - lines <= 150,
            $"{file} is {lines} lines but its ceiling is {ceiling} — the slack ({ceiling - lines}) exceeds a "
            + "tranche. Lower the Ceilings entry to the measured size so the next regression is caught.");
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
