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
    // 3,204 → 3,190 with the Contacts and Calendar tabs ADDED (#564, ADR 0624): their markup would have cost
    // +18, so the bottom tab bar was extracted into <WorkbenchTab> first. It was fifteen copies of the same
    // four lines differing in an icon, a label and a tour id — which is why every tab added cost the shell
    // another four. Paying rather than raising is the point of this guard: the growth got argued for, and the
    // argument produced a component instead of a bigger number.
    // 3,190 → 3,196 for the notebook affordances (#564, ADR 0625): two menu entries and the `@if` that gates
    // them on the rels the row advertised. The BEHAVIOUR cost nothing here — it went to DocumentActions, where
    // the other row actions already live — so what the shell is charged for is six lines of menu markup, which
    // is what a menu entry genuinely costs. A component wrapping two MudMenuItems would have bought the number
    // back at the price of a file that exists only to hold them.
    // 3,196 → 3,204 for #634: the `folders` rel gate around "New subfolder", so the entry is absent
    // where the server would refuse it rather than present and failing. Shell-level because the tree
    // pane's node menu is supplied by the shell, not owned by the pane.
    // 3,204 → 3,212 finishing that gate, and it is the same interaction again: the raise is eight lines, of
    // which three are code. NewFolderAsync now takes the node it acts on (the menu passed the row while the
    // handler read the selection — ADR 0559), and NavigateToFolderAsync keeps the LINKS of the resource it was
    // already fetching instead of only its name, which is what made every rel-gated affordance read as
    // unavailable after a Go to. Both are one-line changes wearing the comment that explains a non-obvious gate.
    // 3,212 → 3,217 for #634's last part (ADR 0637): Upload joins New folder on the `create-child` rel — the
    // ribbon button, and the tree menu entry which now needs an `@if` around it. Five lines, three of them the
    // markup a gated menu entry costs; the drop-zone half of the same change cost the shell nothing, because it
    // lives in BrowseService and ContentsListPane where the attributes are decided.
    // 3,216 → 3,225 for #673: the creates are now built from what the folder ADMITS — one loop over a
    // server-supplied list, replacing the per-rel `@if`s the two raises above paid for — nested under a "New"
    // submenu that matches the desktop (ADR 0511). Nine lines, and only four of them markup: the loop is
    // SHORTER than what it replaced, and would stay that length if a tenant-authored folder mask arrived
    // tomorrow. What is charged here is the comment saying why "New" is a submenu rather than a flat list,
    // which is the decision a reader would otherwise re-litigate — the label is the mask's own name, so a flat
    // entry reads as a noun among verbs, and no client can prefix a verb onto it across four languages.
    // Five of those lines are a note that cost three suite runs to write: a submenu is a nested MudMenu (a
    // MudMenuItem wrapping items renders NOTHING) and takes StartIcon, not Icon (which renders a bare
    // icon-button activator with no label). Both compile clean, so the build says nothing and the menu is
    // silently wrong — the kind of thing the next person pays for again unless it is written down here.
    // 3,230 → 3,299 for the tablet tier (#684): a media query for a tablet held upright (folded into the
    // single-pane block as a second condition rather than a copy of it), one for a tablet held sideways, and
    // the viewport-mode callback that replaced the phone bool.
    //
    // The biggest raise this guard has taken, and argued rather than assumed. EXTRACTION WAS CONSIDERED AND
    // REJECTED: ADR 0491 records that these queries must come LAST in this <style> block to beat the base
    // .wb-* pane widths at equal specificity, and an external sheet in <head> loads EARLIER, which inverts
    // that. Moving them would trade 90 lines for a cascade bug the tests would not obviously catch.
    // The comments were trimmed by 12 lines first; what is left is the third tier of a layout the shell owns.
    // 3,299 → 3,313 for #686: the tree marks the SELECTED node rather than the open folder, and a folder row
    // selection reveals it. Fourteen lines, of which four are code — the reveal itself went to TreeState,
    // which owns the tree's shape, rather than here. What is charged is the selection highlight's CSS and the
    // note on why an outline and not a fill (the tree already spends its accent on the glyphs, ADR 0581).
    // 3,313 → 3,331 for #692: the reveal now brings the marked node into view. Eighteen lines, of which four
    // are code — the flag, its consumption after the render that applies the mark, and the guarded interop
    // call. The scroll arithmetic itself is in wbLayout.js, where the DOM measuring belongs.
    // 3_307 -> 3_322 for #691, owner-confirmed: the detail pane's workflow slot now renders the transitions the
    // SERVER advertises instead of a permanent "Start workflow" button. Fifteen lines, and they are the shell's
    // genuine share — following the current version's workflow rel during the detail load, clearing it with the
    // other addresses (ADR 0559), and reselecting after a transition, which needs SelectItemAsync. It was +33
    // until the act-or-dialog decision moved to DocumentActions, which is where per-document actions live.
    //
    // 3_331 -> 3_307: the per-document action row moved into DocumentActionsRow (ADR 0664). It went the RIGHT
    // way under pressure — adding Compare versions to that row put the shell 16 lines over, and this guard's
    // own advice ("put the new code in the component that owns the responsibility") was cheaper than the
    // exception it offers as the alternative. (The extracted row takes its state as parameters rather than
    // injecting it, which costs five lines here and is what makes it re-render at all — see the component.)
    // 3_322 -> 3_331 for #702 PR 3, owner-confirmed: the tree context menu gains "Take over…", drawn only where
    // the listing advertised the rel. The action itself lives in DocumentActions — what is here is the menu
    // item and its gate, which is the one part that cannot live anywhere else while the menu does.
    // 3,331 → 3,353 for #703 (owner-confirmed 2026-08-23): the duplicate-address-claim question in
    // SaveDetailAsync — show the localized MessageBox, on yes re-save with the confirmation — plus the whoami
    // flag reaching DetailCatalogs. Outcome reporting is explicitly this shell's job (ADR 0558), and the
    // ask-and-retry it reports on lives in DetailEditor.
    // 3,353 → 3,360 for #704 (owner-confirmed 2026-08-23): PrepareUploadAsync takes the .eml header text
    // from JS and puts the shared-extracted Message-ID onto the duplicate probe. Seven lines, half comment.
    // 3,360 → 3,392 for #686 (ADR 0703, owner-confirmed 2026-08-26): the ring marks the open folder, and
    // deselecting exists. The same interaction as every raise above — it was +49, and the trim took it to +32
    // by moving the reasoning into the ADR and leaving only the code. What is charged here is shell
    // coordination and nothing else: the shell owns the selection, so clearing it, and describing the folder
    // when nothing is selected, are its own work. The gesture that reports it went to ContentsListPane, and
    // the choice of node went to the desktop's OpenFolderMark.
    // 3,392 → 3,398 for #768 (owner-confirmed 2026-08-26): six lines, and all six are the grid TRACKS and the
    // breakpoint that collapses the new Owner column first. The column itself — header, cell, sort — went to
    // ContentsListPane, which is where a list row belongs; what the shell is charged for is the layout, which
    // is what the shell owns.
    // 3,398 → 3,417 for the selection-survives-its-load fix found while finishing #768/#769: a row selected
    // while a folder was still loading was silently discarded when the rows arrived, emptying the detail and
    // preview panes with no error. Shell-level by definition — the shell owns the selection and owns the load
    // that was clobbering it.
    // 3,417 → 3,426 for the same fix's second half (#811): the survival rule covered document rows only, and
    // a FOLDER row clicked during the reload was reverted to the parent. Same seam, same owner, same reason.
    private const int Ceiling = 3_426;

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
