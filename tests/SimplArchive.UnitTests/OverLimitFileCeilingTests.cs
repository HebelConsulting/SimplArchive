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
// DocumentsController left the list with the PR that added the guard (2,613 → 838, ADR 0571); the tail —
// WebDavMiddleware (1,762 → 964), MainWindow.axaml.cs (1,945 → 967) and HighlightOverlay (1,227 → 995) —
// followed one PR later (ADR 0572). What remains is the two desktop giants, each its own multi-session
// program: measure, split by responsibility, then delete the entry.
//
// One caveat ADR 0572 records: MainWindow's CLASS still spans ~1,575 lines across its three partial files —
// the per-feature partial split for view-glue was the user-approved shape, so the file-level ceiling is what
// this guard holds there.
public class OverLimitFileCeilingTests
{
    // File → its ceiling, measured when the guard was added (or last lowered). Growth fails; a deliberate
    // tranche lowers the number in the same commit. When a file passes under 1000, delete its entry.
    private static readonly Dictionary<string, int> Ceilings = new()
    {
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.cs"] = 6_990,
        ["src/SimplArchive.DesktopClient/Services/SimplArchiveApiClient.cs"] = 4_523,

        // The four that crossed the line AFTER #466's list was written — proof the debt grows invisibly
        // without a guard, which is why they enter it the moment they were noticed (full sweep, 2026-08-13).
        // MainWindow.axaml is pure markup; whether the 1000-line rule covers markup-only .axaml is undecided,
        // but a ceiling costs nothing while that question waits.
        ["src/SimplArchive.DesktopClient/Views/MainWindow.axaml"] = 2_450,
        ["src/SimplArchive.DesktopClient/Program.cs"] = 1_253,
        ["src/SimplArchive.Api/Controllers/InboxController.cs"] = 1_071,
        ["src/SimplArchive.Api/Controllers/DocumentVersionsController.cs"] = 1_067,
        ["src/SimplArchive.Api/Controllers/UsersController.cs"] = 1_011,
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
