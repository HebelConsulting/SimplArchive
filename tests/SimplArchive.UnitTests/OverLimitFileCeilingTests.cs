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
// followed one PR later (ADR 0572). The three barely-over Api controllers and Program.cs left on 2026-08-14
// (issue #466, closed that day), which is when the owner also interviewed on everything remaining. What the
// list holds now is exactly those decisions: the two desktop giants — MainWindowViewModel (per-tab
// view-model tranches, then re-interview at the measured floor, #517) and SimplArchiveApiClient (real
// per-area clients sharing one auth/HTTP core, planned together with #443, #518) — and MainWindow.axaml,
// where the owner DECIDED markup counts: a UserControl per tab down to <1000 (#519).
//
// One caveat ADR 0572 records: MainWindow's CLASS still spans ~1,575 lines across its three partial files —
// the per-feature partial split for view-glue was the user-approved shape, so the file-level ceiling is what
// this guard holds there.
public class OverLimitFileCeilingTests
{
    // File → its ceiling, measured when the guard was added (or last lowered). Growth fails; a deliberate
    // tranche lowers the number in the same commit. When a file passes under 1000, delete its entry.
    //
    // …and a file that crosses back over RE-ENTERS. Deleting an entry is what the list is for, but it also
    // removes the only thing watching that file: this guard tracks known offenders, not every file, so a file
    // burned down to 967 can climb back over 1000 with nothing objecting — which is exactly what
    // MainWindow.axaml.cs did (967 → 1,156, unnoticed, ADR 0613). Re-entering it restores the ratchet for that
    // file; the general rule (every authored file measured against 1000) is still the open backlog item, and
    // this entry is a patch over one instance of the gap rather than a fix for it.
    private static readonly Dictionary<string, int> Ceilings = new()
    {
        // 7,009 → 7,025 for the Contacts tab's wiring (#564, owner-confirmed 2026-08-17), and the debt that
        // raise promised is now PAID: → 6,998, below where it started, with the Calendar tab added on top.
        // OnSelectedTabChanged's fourteen `if (value == n)` blocks became one switch expression, which is what
        // the file was really being charged for — the chain made every new tab cost another dozen lines, so
        // the growth was structural rather than per-feature (#517). Lowered rather than left with headroom: an
        // unlowered ceiling is permission to grow back into it, silently.
        // → 7,021 for the notebook affordances (#564, owner-confirmed 2026-08-17): "New section" / "New note"
        // need two gating flags and two creates. The three creates were unified into ONE body first —
        // CreateSubfolder now shares it and shrank to a single line — so what is charged here is the feature,
        // not a third copy of a method that already existed twice.
        // 7,021 → 7,049 for #634: one ScratchParentAsync helper replacing the assumption that scratch space
        // lives on Tree[0], which six self-tests each made separately, plus the TreeContextCanAddFolder
        // property that gates "New subfolder" on the rel. Raised deliberately rather than by accident.
        // 7,049 → 7,055: the ribbon's New folder now answers from the OPENED folder's `folders` rel rather than
        // being true for any folder at all — cleared on entry and decided once its links arrive, because the
        // button is clickable throughout the load (ADR 0559). Six lines, all of it that decision and its why.
        // 7,055 → 7,063 for #634's last part (ADR 0637): UploadDroppedFilesAsync now refuses without the
        // `create-child` rel. Not belt-and-braces over the two gates in the view — it is the ONLY thing covering
        // a drop on the empty list area, which falls back to the currently-open folder.
        // 7,063 → 6,843: #517 tranche 1 — the Audit tab's state moved to AuditTabViewModel (the
        // CheckoutTabViewModel shape). Only the CanViewAuditLog visibility gate stays.
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.cs"] = 6_843,

        // SimplArchiveApiClient left the list with #443's ops tranche (4,527 → ~420: nine area clients on one
        // ApiCore). What remains over-limit is the largest single area it produced: the documents area itself —
        // OWNER-CONFIRMED as an accepted exception (2026-08-17, on #443's close): split further only if a real
        // seam appears; the finale already took the obvious ones (#518 owns any future burn-down).
        // → 1,511 for the section/note creates (#564, owner-confirmed 2026-08-17). Half the raise it would have
        // been: CreateFolderAsync had the same body, so it now shares the new helper instead of sitting beside
        // a near-duplicate — +13 rather than +26.
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.cs"] = 1_511,

        // Re-entered 2026-08-17 (ADR 0613): burned down to 967 in an earlier pass, back to 1,156 since — the
        // handlers here are what #519 moves into per-tab UserControls, which is what takes it under again.
        // The +4 for Help ▸ Show log folder is OWNER-CONFIRMED (2026-08-17): the handler belongs beside
        // OnOpenManual and OnShowAbout, and #519's tranche moves all three together.
        // → 1,191 for the two notebook context-menu handlers (#564, owner-confirmed 2026-08-17). The cheaper
        // route was considered and declined here: six handlers repeat the same vm/node guard, and extracting it
        // would net about -16, but it rewrites five handlers this feature does not touch. #519 moves all of
        // them into per-tab UserControls, and that is where the guard should be collapsed once rather than
        // twice.
        // 1,191 → 1,192: one line reading the new `folders` rel off the right-clicked node (#634).
        // 1,192 → 1,194: the tree's create now FOLLOWS that rel instead of `children`, plus the two-line note
        // saying why the gate and the address must be the same rel.
        // 1,194 → 1,147: #519 tranche 1 — the audit export/purge handlers moved into AuditTab.axaml.cs with
        // the markup they serve, exactly the per-tab collapse the notes above kept deferring to.
        ["src/SimplArchive.DesktopClient/Views/MainWindow.axaml.cs"] = 1_147,

        // The four that crossed the line AFTER #466's list was written — proof the debt grows invisibly
        // without a guard, which is why they enter it the moment they were noticed (full sweep, 2026-08-13).
        // MainWindow.axaml is pure markup, and the owner DECIDED (2026-08-14, #519) that the 1000-line rule
        // COVERS markup: the target is <1,000 via a UserControl per TabItem.
        // 2,464 -> 2,427 (ADR 0577: the Intray ribbon became its own control) -> 2,330 (ADR 0578: so did the
        // top bar). Both had gained a responsibility per feature while living here; chrome and a ribbon are
        // things, not regions.
        // 2,032 → 1,956: #519 tranche 1 — the Audit TabItem's body became AuditTab (the TenantSettingsPane /
        // ContactsTab shape); the header and its visibility gate stay with the shell.
        ["src/SimplArchive.DesktopClient/Views/MainWindow.axaml"] = 1_956,
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
