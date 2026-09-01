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
// per-area clients sharing one auth/HTTP core, planned together with #443, #518) — and — until 2026-08-31 —
// MainWindow.axaml, where the owner DECIDED markup counts: a UserControl per tab down to <1000 (#519). It
// LEFT the list at 2,327 → 574, by exactly that route: a control per tab (Audit, Recycle bin, Check-out,
// Search), then the Repositories tab split into its ribbon, tree, contents and detail panes. The last four
// inherit the window's DataContext rather than getting a view-model of their own, because Repositories IS
// the shell — 138 binding roots against Search's 31.
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
        // 6,843 → 6,849 for #673: the three per-kind create flags became one collection of server-supplied
        // entries plus a visibility bool. Six lines, and all of it the note explaining why the entries are a
        // submenu — the flat alternative was a fifteen-entry rewrite nothing could verify without opening the
        // menu by hand. The property count went DOWN; the comment is what costs.
        // 6,849 → 6,851 for per-mask icons: two assignments, one on the list row and one on a search hit, so
        // an object wears the same glyph in every pane that draws it. Two lines, both of them the feature.
        // 6,851 → 6,899 for #686: the detail pane now describes a selected FOLDER, which it did not — it kept
        // the previous document's values on screen, which is worse than an empty pane because it looks right.
        // The 48 lines are that branch, the version-less path through LoadSystemFieldsAsync, and the
        // superseded-load guard: two loads race when a user selects twice quickly, and the EARLIER one could
        // finish last and repaint the pane with the previous subject. That race predates this issue and was
        // found by its test.
        // 6,899 → 6,913 for remembering the tree's expansion between sessions: a field, the loop that wires
        // the roots, and the note on why only roots need wiring. The feature itself is ~110 lines and they are
        // NOT here — TreeExpansionMemory owns them. Written inline first, which is exactly what this guard
        // caught: +110 on a file already on the debt list, cut to +14 by giving the responsibility a home.
        // 6,913 → 6,973 for #696: marking the selected folder in the tree without opening it. Sixty lines —
        // MarkInTreeAsync, the event the view scrolls from, and the recursive walk that clears the previous
        // mark. Left here rather than extracted: unlike the tree's remembered shape (which became
        // TreeExpansionMemory), this is three short members reading state this class already owns, and a class
        // holding one bool would be a file that exists to satisfy a number.
        // 6_973 -> 6_987: CreateStructuredChildAsync (#689) joins CreateSubfolderAsync / CreateSectionAsync /
        // CreateNoteAsync, forwarding to the same generic with a lambda. Owner-confirmed: splitting one of four
        // siblings out would scatter a family this file already documents as belonging together.
        // 6_987 -> 6_991 for #691 (owner-confirmed): the load call plus two clears. The ~90 lines the feature
        // actually cost went to MainWindowViewModel.TabSelections.cs, which is not on this list.
        // 6,982 → 6,996 for #703 (owner-confirmed 2026-08-23): the ConfirmDuplicateClaimDialog view-provided
        // delegate (the AnnotationDialog pattern) and the declined-question catch. The ask-and-retry
        // choreography itself went to DocumentsClient — this is only the seam the view plugs a dialog into.
        // 6,996 → 7,002 for #704 (owner-confirmed 2026-08-23): the .eml gate + the shared Message-ID
        // extraction joining the duplicate probe. The extraction itself lives in Presentation; this is the
        // upload flow's six-line share until #517's per-tab burn-down reaches it.
        // 7,002 → 7,020 for #686 (owner-confirmed 2026-08-26): the mark now follows the OPEN folder rather than
        // the selected row, superseding #696's behaviour above. The owner chose extraction over a bare bump, so
        // the reasoning, the tree walk and the folder-as-row construction went to OpenFolderMark — which turned
        // a +67 change into +16. What is left cannot move: a bindable ClearListSelection command, the
        // nothing-selected branch of the selection handler, and two call sites in the contents load.
        // 7,020 → 7,041 for #768 (owner-confirmed 2026-08-26, the second raise on this file today): the owner
        // column, and the target's list-row columns on a REFERENCE row. A referenced row was drawing blank
        // Type / Doc date / Size / Tags cells beside a real row that filled them, on both clients, because a
        // reference was projected as a stub. Assigning the columns is what a row costs; the projection itself
        // became one shared definition (DocumentSummaryQueries) rather than a second copy.
        // 7,041 → 7,070 for #858's destructive gating (owner-confirmed 2026-08-30): four TreeContext* flags plus
        // CanRenameSelected/CanDeleteSelected, set from the right-clicked node. Raised rather than extracted
        // because MainWindow's menu bindings read these properties directly — moving them to another type would
        // put a gate at arm's length from the menu it gates, which is the coupling this change exists to remove.
        // #517's per-tab view-model burn-down is still the plan for this file.
        // #517 split the Intray tab out into MainWindowViewModel.Intray.cs. That LOWERS the number below without
        // lowering the class, so the new partial is listed too — otherwise this guard would reward moving cost
        // to an unwatched file, which is the exact regression its header warns about. The class total is what
        // #517 is actually burning down; these two entries only stop either half growing.
        // 6,070 → 6,032, and the Intray entry is GONE: that partial became IntrayTabViewModel, a class of its
        // own (683 lines, under the limit and covered by the general principle). This is the first tranche that
        // moves cost OUT of the class rather than between its files — 7,330 → 6,795 across every partial. The
        // window's half of the seam is its own partial and is listed below, for the reason above: a new file
        // nothing watches is how the next tranche would quietly become a relocation.
        // 6,032 → 6,015 for #517, then → its measurement here as the tab work continues.
        // The ShellContext partial LEFT this list: it was added in #903 to stop cost moving to an unwatched
        // file, and ADR 0732's general check now measures every authored file, so a 107-line partial has no
        // business in an override list for files at or over 1000.
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.cs"] = 6011,

        // DocumentsClient is GONE from this list: 1,235 -> 992, by #518's plan -- real per-area clients sharing
        // the one authenticated ApiCore. Four areas left it:
        //
        //   ReferencesClient (159, new)          shortcuts: what a folder references, what references an item
        //   RecycleBinClient (91 -> 155)         the SINGLE-item restore/purge/list joined the tenant-wide ones
        //   RepositoryArchiveClient (66, new)    export a subtree to a .zip, import one back
        //   TagsClient (50, new)                 a document's tags + the tenant catalog
        //
        // The recycle-bin move was a REPAIR, not a relocation: this client already owned the tenant-wide bin
        // while the per-item operations sat in DocumentsClient, so RecycleBinTabViewModel called TWO clients to
        // drive ONE tab and had to know which answered what. Restore/Purge already took IAdvertisesLinks so one
        // implementation served both row types -- a generic over both belongs with both.
        //
        // SetPrimaryLocationAsync deliberately STAYED. Promoting a referencing folder to primary is a reparent
        // sharing MoveAsync's If-Match contract, and it is one of several paths that change a document's
        // parent; a rule about reparenting that lives in one of two files gets applied in one of two places.
        //
        // Recorded because the estimate was wrong and the owner overruled the recommendation: the first two
        // areas were sized at ~260 lines and were 169, leaving 1,066. Reaching 992 took TagsClient, a 50-line
        // file this guard's own header would call "a file that exists to satisfy a number". That objection was
        // raised and the owner chose to clear the entry anyway (2026-09-01). If a later reader thinks TagsClient
        // is too small to justify itself, they are not the first -- but the tags resource IS one address read
        // or replaced (ADR 0719), so it is at least a real subject and not an arbitrary cut.

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
        // 1,147 → 1,132: #673 replaced OnTreeNewFolder/OnTreeNewSection/OnTreeNewNote with ONE
        // CreateAdmittedAsync. The address, the label and which question to ask now come from the entry the
        // server sent, so three handlers collapsed into one with no case per family. Lowered, not left with
        // headroom.
        // 1,132 → 1,136 for per-mask icons on the New menu: the entry now wears the glyph the created thing
        // will wear. One line of code and three of comment, because "menu says Calendar, tree draws a
        // calendar" is the reason the fallback is not simply the generic add-glyph.
        // 1,136 → 1,137: one line of the note explaining why the New submenu's FALLBACK is a plain kind
        // glyph rather than an add-glyph — the verb is on the parent, so a plus made Folder read as a
        // different sort of action from its siblings. Found by looking at the built menu, which is the only
        // way it could be found.
        // 1,137 → 1,145 for #696: bringing a newly marked tree node into view. Only the VIEW knows which
        // container renders which node, so the view-model raises and this scrolls — eight lines, four of them
        // the note on why BringIntoView is the right primitive (minimal movement, and a no-op when already
        // visible, which is the behaviour decided for the web).
        // 1_145 -> 1_156: the tree menu's rich creates (#689) went to Views/TreeCreateDialogs.cs rather than
        // into this file, which is why eleven lines land here instead of fifty-seven. Adding to an over-limit
        // file needs the owner's confirmation (CLAUDE.md); the confirmation given was to extract.
        // 1_156 -> 1_177 for #691 (owner-confirmed): OnWorkflowTransition reads the pressed rel and routes
        // reject/reassign to the workflow window, which needs this window as the dialog owner and so cannot
        // leave the code-behind.
        // 1,194 → 1,199 for #703 (owner-confirmed 2026-08-23): wiring the duplicate-claim ConfirmDialog to
        // the view-model's delegate — dialogs live in code-behind by standing pattern.
        // 1,199 → 1,216 for #686 (owner-confirmed 2026-08-26): deselecting. An Escape case in the list's
        // EXISTING key handler, and a tunnel-phase pointer handler for a press that landed outside any row.
        // Both are input plumbing for a control this file already handles; an attached behaviour would scatter
        // one list's input across two places for the sake of the number.
        ["src/SimplArchive.DesktopClient/Views/MainWindow.axaml.cs"] = 1_190,


        // Entered 2026-08-20 (#673, ADR 0655) — over the line since well before it was noticed, and never on
        // the list, so nothing was watching it. It enters ON THE WAY DOWN: the containment port took it 1,041 →
        // 1,017 by moving the decision into MaskContainmentRules, and the entry is what stops that gain being
        // given back a few lines at a time. Which is the whole lesson of this list: the file was not noticed
        // because no single commit made it look unreasonable.
        //
        // The direction of travel is right — every invariant here that grows a real collaborator can leave the
        // same way containment did, since what the DbContext owes is the ENFORCEMENT POINT, not the rules. No
        // burn-down is scheduled and none is promised; lower this when one happens.
        // The four that crossed the line AFTER #466's list was written — proof the debt grows invisibly
        // without a guard, which is why they enter it the moment they were noticed (full sweep, 2026-08-13).
        // MainWindow.axaml was one of them and has since LEFT the list (2,327 → 574, #519); what follows is
        // the rest of that group.
        // 1,017 → 1,015: the shared containment provider (#673) replaced the private cache and its one-use
        // wrapper. Lowered rather than left with headroom — an unlowered ceiling is permission to grow back
        // into it, silently. It also caught this change growing the file by ONE line, which is the entire
        // argument for the entry existing.
        // 1,018 → 949 for #703, and it is the "direction of travel" paragraph above happening: the e-mail
        // arm would have taken this file to 1,030, so the whole per-type format/range validator left instead
        // (FieldValueValidation, 80 lines). It was a pure function over two entities that never touched the
        // context's state — the DbContext still owns the ENFORCEMENT POINT, which is what it owes; it did not
        // owe the rules. Guard-prompted, not planned: the failure named the choice and the cheaper half won.
        // 949 → 898: the structural-mask immutability rule brought DocumentMaskInvariants with it, taking the
        // mask-side invariants (immutability + the repository/mask lockstep) the same way the validator left.
        // Two extractions from two branches, merged: the file is UNDER 1,000 for good now, and the entry stays
        // (rather than being deleted at the 1,000 threshold) until the ratchet's general rule exists — a file
        // this central re-crossing the line deserves to fail a build, not a review.
        ["src/SimplArchive.Infrastructure/Persistence/SimplArchiveDbContext.cs"] = 898,

        // ---- Re-entered 2026-09-01 (issue #909) -------------------------------------------------------
        // Eight authored files were at/over 1000 with no entry here and no exception on record. That is this
        // guard's OWN stated failure mode — its header says a file burned down to 967 can climb back over with
        // nothing objecting, and five of these did exactly that: four regrew after a deliberate burn-down, two
        // were born over the limit in new features. WebDavMiddleware is that sentence with the biggest number.
        //
        // Re-entering is NOT granting an exception (which is the owner's alone) and NOT calling the debt paid.
        // It restores the ratchet at today's measurement so the number cannot rise further while each file
        // waits for its own answer — burn-down, or an owner-confirmed exception recorded here.

        // WebDavMiddleware is GONE from this list: 1,601 → 299, burned down by moving every verb handler out
        // (WebDavReads / WebDavWrites / WebDavMoveCopy), leaving the middleware as auth + dispatch. Deleting its
        // entry is safe now in a way it was NOT before — the general check below means a file under the limit is
        // still measured. Last time this file left the list, at 964 under ADR 0572, nothing watched it and it
        // came back at 1,601. That is the whole reason the general check exists.

        // Born over the limit (2026-08-17, #561) and never watched. Home.razor has WorkbenchShellSizeTests, but
        // the tab components EXTRACTED from it inherited no guard — the extraction moved the lines out of the
        // watched file and into an unwatched one, which is the shape this guard exists to catch.
        ["src/SimplArchive.Client/Components/Tabs/IntrayTab.razor"] = 1_482,

        // DesktopClient/Program.cs is GONE from this list: 1,237 → 672. The entry above it had already named the
        // cause — "over the limit BECAUSE all 28 headless hooks are inline in it" — and the remedy it had been
        // paying in instalments (one hook moved out per new hook) was never going to close a 237-line gap. The
        // two FAT bodies were two thirds of the excess and were not hooks at all: rendering a screen
        // (ScreenshotRenderer, 265 lines) and driving the api-client (ApiClientChecks, 264) are each their own
        // responsibility, and neither is dispatching a command line, which is what Main is for. The small
        // per-hook bodies stayed: they are 5–45 lines each, they read as the dispatch chain they belong to, and
        // ~20 files holding one check apiece would be a worse file to read than the one they came from.
        //
        // Deleting the entry is safe in a way it was NOT at #520, when this file left the list at 987 and came
        // back at 1,287 with nothing watching it: Every_authored_file_over_the_limit_has_an_entry now measures
        // every authored file, so a re-crossing fails the build rather than going unnoticed for three months.

        // RepositoryImporter is GONE from this list: 1,050 -> 865 (it was 907 at the #466 close and crossed
        // quietly, which is the usual way). ArchiveIdentityMapper took the four methods that answer "who, or
        // what, is this archive's principal / mask / label in THIS tenant?" plus the ten format records they
        // speak in. That is a different job from walking a zip and writing documents: not one of them knows
        // about archive entries, blobs, the four import phases, or the transaction they run inside.
        //
        // Two things deliberately did NOT move, and both are the interesting half of the split. ResolveCreator
        // attributes an unmapped creator to the IMPORTING admin, which arrives after construction via
        // SetImporter — it reads the importer's own mutable state rather than answering a question about the
        // archive, so moving it would mean threading that state across purely to keep a method family together
        // (ADR 0730). And UniqueName has callers on BOTH sides, so it became internal rather than being copied:
        // its subject is naming a document among its siblings, which is the importer's job, and one shared pure
        // function beats a second copy that drifts.

        // ImapWrites is GONE from this list: 1,022 -> 725. It was born over the limit (2026-08-17, #562 slice 3)
        // with no exception on record, and what it was carrying was two subjects: the MESSAGE writes (APPEND,
        // EXPUNGE, MOVE/COPY) and the MAILBOX lifecycle (CREATE, DELETE, RENAME). The second moved to
        // ImapMailboxes (597 -> 903), where LIST, STATUS and SELECT already read the catalog those commands
        // mutate.
        //
        // The file's own header comment is what gave it away: it described APPEND, EXPUNGE, MOVE and COPY and
        // never once mentioned CREATE, DELETE or RENAME. A comment narrower than its file is a boundary
        // somebody drew correctly and then did not keep — so the move made the documentation true rather than
        // needing it rewritten.

        // The three barely-over controllers are GONE from this list — UsersController 1,019 → 855,
        // AclEntriesController 1,007 → 975, DocumentVersionsController 1,001 → 991 — and deleting their entries
        // is safe because Every_authored_file_over_the_limit_has_an_entry now measures them anyway (ADR 0732).
        // This is that check's first real use: before it, "under 1000" meant "unwatched", which is how
        // UsersController and DocumentVersionsController came back after #516 burned them down.

        // tests/ and tools/ are authored too, and the general check below now measures them. Both entered at
        // their measured size on the owner's instruction (2026-09-01).
        //
        // ImapEndpointTests has since LEFT: 1,127 -> 766, by moving the five notes/notebook tests to
        // ImapNotesTests. It was one file covering six unrelated subjects, and the split follows the convention
        // the directory ALREADY had — ImapSearchTests, ImapStandingMailboxTests, ImapAttachmentFetchTests and
        // ImapReferenceMailboxTests were each their own file. ImapEndpointTests was simply the one nobody ever
        // divided, which is how a file named for an endpoint ends up meaning "the IMAP tests that predate the
        // habit of naming them".
        //
        // A test file earns the same rule as product code for the same reason: the fifteen tests here shared
        // nothing but a fixture, so the only thing the single file bought was a longer scroll.

        // EloIxPorter/Program.cs is GONE from this list: 1,233 -> 865. Same disease as DesktopClient/Program.cs
        // and the same cure: every probe's BODY lived inline in the `if (args is ["--x", ..])` dispatch chain.
        // Six probes on ONE subject -- the external system's notes/annotation investigation -- moved to a
        // NoteProbes class (412), and the chain kept the rest.
        //
        // Their usage comments went with them, which is half the point. Three had DRIFTED to the top of the
        // file and sat above unrelated blocks: `--notes`' description was above `--pdf-dims`' code, and
        // `--probe`'s was 860 lines from its own. An inline dispatch chain is exactly where a comment quietly
        // stops being next to the thing it documents, because nothing moves when a block is inserted above it.

        // NOT listed, deliberately: Home.razor (3,390) has its own richer guard, WorkbenchShellSizeTests, and
        // this file's header is explicit that one guard per file is the rule — two guards on one file will
        // eventually disagree. RepositoriesController left the list under its own steam at 977 (#911).
    };

    // The trees whose files are AUTHORED. Generated output is excluded below rather than here, because the
    // exclusions are about what produced a file, not where it lives.
    private static readonly string[] AuthoredTrees = ["src", "tests", "tools"];

    /// <summary>
    /// Over the limit, and measured by a DIFFERENT guard. Not exceptions — the opposite: these carry a richer
    /// check than a line ceiling, and listing them here as well would put two guards on one file, which this
    /// file's header says will eventually disagree.
    /// </summary>
    private static readonly Dictionary<string, string> GuardedElsewhere = new()
    {
        ["src/SimplArchive.Client/Pages/Home.razor"] = "WorkbenchShellSizeTests (size + the slack test this guard omits)",
    };

    /// <summary>
    /// The general rule, inverted from the list above: EVERY authored file must be under 1000 lines unless it
    /// has an entry — rather than only the files someone remembered to list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the list could not do on its own. It tracks known offenders, so a file born over the limit
    /// (IntrayTab.razor at 1,482, ImapWrites at 1,022) or one that regrows after leaving (WebDavMiddleware,
    /// 964 → 1,601) was invisible to it — and both happened, which is issue #909. With this check the list
    /// becomes an OVERRIDE list: to exceed 1000 you must add an entry, in the commit that does it.
    /// </para>
    /// <para>
    /// It also makes DELETING an entry safe. Before, deleting one removed the only thing measuring that file;
    /// now the general rule catches it the moment it crosses back.
    /// </para>
    /// <para>
    /// Generated files are not authored and are excluded: EF migrations and their designer/snapshot files, and
    /// <c>*.g.cs</c>. Everything else in the three trees is measured.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_authored_file_over_the_limit_has_an_entry()
    {
        var root = RepoRoot();
        var unlisted = new List<string>();

        foreach (var tree in AuthoredTrees)
        {
            var treePath = Path.Combine(root, tree);
            if (!Directory.Exists(treePath))
            {
                // Withheld from the public mirror (ADR 0484). Nothing to measure, and that is by design.
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(treePath, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path);
                if (ext is not (".cs" or ".razor" or ".axaml"))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal)
                    || rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.Contains("/Migrations/", StringComparison.Ordinal)
                    || rel.EndsWith(".Designer.cs", StringComparison.Ordinal)
                    || rel.EndsWith(".g.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(path).Length;
                if (lines >= 1000 && !Ceilings.ContainsKey(rel) && !GuardedElsewhere.ContainsKey(rel))
                {
                    unlisted.Add($"{rel} ({lines} lines)");
                }
            }
        }

        Assert.True(unlisted.Count == 0,
            "These authored files are at or over the 1000-line limit with no entry in Ceilings:\n  "
            + string.Join("\n  ", unlisted.OrderBy(f => f, StringComparer.Ordinal))
            + "\n\nCLAUDE.md's standing principle is that no hand-written class exceeds 1000 lines and that an "
            + "exception is NOT yours to grant. Split the file by responsibility — or, with the owner's explicit "
            + "confirmation, add it to Ceilings at its measured size in this same commit, with the reason.");
    }

    public static TheoryData<string> Files => [.. Ceilings.Keys];

    [Theory]
    [MemberData(nameof(Files))]
    public void An_over_limit_file_does_not_grow(string file)
    {
        var root = RepoRoot();
        var tree = file.Split('/')[0];
        if (!Directory.Exists(Path.Combine(root, tree)))
        {
            // The public mirror withholds tools/ (ADR 0484), so an entry there has nothing to measure. Skipping
            // a MISSING TREE is not the same as skipping a missing file: the file below still has to exist.
            return;
        }

        var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
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
