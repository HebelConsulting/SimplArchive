namespace SimplArchive.UnitTests;

// A CEILING on the workbench page's size, so it cannot drift back upward between decomposition tranches
// (ADR 0558; CLAUDE.md's 1000-line standing principle).
//
// Home.razor reached 9,479 lines, and it did so the way every file like it does: nobody added a thousand lines,
// everybody added twenty. No single commit looked unreasonable, and there was nothing that made the growth
// visible at the moment it happened. This test is that thing. It is the only mechanism here that acts BETWEEN
// tranches, which is when the regression would occur.
//
// Deliberately a ceiling, not the hypermedia budget's assert-and-lower ratchet. The trade is real and was
// chosen with it stated: a ceiling costs no bookkeeping commit when a tranche lands, but a landed tranche leaves
// the number stale — it no longer states the real figure, only a bound the file is under. Lower it when you
// notice; the guard still does its one job either way.
//
// THE FINISH LINE IS NOT 1,000. Extracting every remaining region — tree/list navigation, annotations, bulk
// actions, upload, drop filing, mentions, export/import, rename, WebDAV, workflow, ribbon, check-out — removes
// about 2,300 lines and leaves roughly 1,450: the workbench LAYOUT MARKUP (~940) plus the shell's own
// coordination (~510: the fields, the three lifecycle hooks, SetTab, the selection, the viewport/phone
// handling, the JS interop wiring). That remainder is one thing, and it is a confirmed exception to the
// 1000-line principle at that floor rather than a debt still being worked down. Going below it would mean
// decomposing the layout markup itself, which buys file count rather than cohesion — the alternative ADR 0558
// already rejected in its "code-behind partial classes only" form.
public class WorkbenchShellSizeTests
{
    private const string ShellFile = "src/SimplArchive.Client/Pages/Home.razor";

    // 9,479 at the start of ADR 0558. Lower this as tranches land.
    //
    // RAISED TWICE, deliberately and on the record. The reminder fix (#420, ADR 0559), and the WebDAV ribbon
    // button (#461) — a ribbon affordance genuinely belongs to the shell, since the ribbon IS the shell. Both
    // times the guard forced the growth to be argued for and both times it shrank first: commentary that
    // belonged in a commit message came out, and only the code stayed. A
    // one-line expression-bodied handler became a real method with a fetch fallback, because taking the address
    // from pane state was a bug that made "Set reminder" silently do nothing. The guard did its job here — it
    // caught +16, most of which was commentary that belonged in the commit message, and only the 11 lines that
    // are actually code survived the trim. That is the intended interaction: growth has to be argued for, not
    // noticed later.
    private const int Ceiling = 3_205;

    [Fact]
    public void The_workbench_shell_does_not_grow()
    {
        var path = Path.Combine(RepoRoot(), ShellFile);
        Assert.True(File.Exists(path), $"{ShellFile} not found — if the shell moved, update ShellFile.");

        var lines = File.ReadAllLines(path).Length;

        Assert.True(lines <= Ceiling,
            $"{ShellFile} is {lines} lines, over its {Ceiling}-line ceiling by {lines - Ceiling}.\n"
            + "The workbench page is being decomposed (ADR 0558), so it may only get smaller. Put the new code "
            + "in the component or service that owns the responsibility — or, if it genuinely belongs to the "
            + "shell, say why and raise the ceiling deliberately rather than as a side effect.");
    }

    // A ceiling far above the real figure asserts nothing while still looking like a guard. This does not force
    // the bookkeeping (that is the ratchet's job, and it was not chosen), but it does make a ceiling that has
    // gone badly stale fail loudly rather than sit there reassuring people.
    [Fact]
    public void The_ceiling_still_describes_the_file()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), ShellFile)).Length;

        Assert.True(Ceiling - lines <= 400,
            $"{ShellFile} is {lines} lines but the ceiling is {Ceiling} — {Ceiling - lines} lines of slack, which "
            + "is room for a whole tranche of new code to land unnoticed. Lower Ceiling to the real figure.");
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
