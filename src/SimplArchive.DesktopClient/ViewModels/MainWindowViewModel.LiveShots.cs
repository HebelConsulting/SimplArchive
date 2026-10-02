using System;
using System.Linq;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// Brings the workbench to the state the manual's leading figure shows, from the REAL app (#1358).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside the fixture rather than instead of it.</b> The published desktop screens came from
/// a hand-written fixture while the web screens were driven live, so one manual described TWO datasets — and
/// the failure is silent by construction, because a fixture cannot fail when the product moves. #1357 fixed the
/// symptom (the fixture now names documents the seeder really files, guarded by
/// <c>ManualFixtureMatchesSeedTests</c>); it cannot catch the larger class, which is a feature the product
/// GAINS that the fixture knows nothing about. The automatic chat entries were exactly that: live in the app
/// and in <c>web-repositories.png</c>, absent from the desktop shot until somebody noticed by eye.
/// </para>
/// <para>
/// The fixture stays, demoted to what it is good at — a Docker-free render smoke for the PR gate, which is why
/// that gate is fast. Owner-decided 2026-09-24 (hybrid).
/// </para>
/// <para>
/// <b>It drives the ordinary paths and nothing else.</b> No populator, no synthetic row: sign in, load the root,
/// open the repository, select the document. That is the entire point — a screen assembled by the same code a
/// user's clicks drive cannot describe a dataset the product does not have. The precedent is the
/// <c>…SelfTestAsync</c> hooks beside it, which already drive this view-model against a running Api.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    /// <summary>The repository the manual's workbench figure stands in, as <c>DemoDataSeeder</c> files it.</summary>
    internal const string LiveShotRepository = "Demo Repository";

    /// <summary>
    /// The folders to walk from the repository down to the document, as <c>DemoDataSeeder</c> files them.
    /// </summary>
    /// <remarks>
    /// <b>The fixture had this wrong, and only a live capture could say so.</b> It lists the invoice as a direct
    /// child of the repository, beside an "Invoices" folder that the seeder does not create at all; the real
    /// filing tree is <c>Business Years / Contracts / General</c>, with this document under
    /// <c>Contracts / Acme Corp</c>. So the published figure showed a folder layout the product does not
    /// produce — which #1357's guard could not catch, because it checks the document NAMES and this is the
    /// structure around them. Exactly the class #1358 exists for.
    /// </remarks>
    internal static readonly string[] LiveShotPath = ["Contracts", "Acme Corp"];

    /// <summary>The document the figure selects — the same one the fixture names, and the web shot shows.</summary>
    internal const string LiveShotDocument = "Invoice 2026-003";

    /// <summary>
    /// Signs in with <paramref name="accessToken"/> and navigates to the figure's state.
    /// </summary>
    /// <remarks>
    /// Named for what it produces rather than for the flag that calls it, because the next live screen will be a
    /// sibling of this method and not a parameter on it: the screens differ by which tab and which row, which is
    /// navigation, not configuration.
    /// </remarks>
    internal async Task PopulateWorkbenchFromLiveAppAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        IsLoggedIn = true;

        // The rights and the notification count come from the server here, where the fixture asserts them. That
        // is one of the things a fixture cannot keep true: CanCreateFolder, IsTenantAdmin and the bell badge are
        // all gated surfaces, and a fixture sets them to whatever made the screenshot look right.
        await SetupUserContextAsync();
        await LoadRootAsync();

        var repository = Tree.FirstOrDefault(n => n.Name == LiveShotRepository)
            ?? throw new InvalidOperationException(
                $"The live app has no '{LiveShotRepository}' at the tree root, so the manual's workbench figure "
                + $"cannot be captured from it. The seed names its repositories in DemoDataSeeder — either it "
                + $"renamed this one (update {nameof(LiveShotRepository)}) or the capture is pointed at an app "
                + "that was not seeded.");

        // THE HIGHLIGHT AND THE LOAD ARE SET SEPARATELY, and both halves are deliberate.
        //
        // `ReselectTreeFolderAsync` was the obvious call and is wrong here: it returns immediately unless the
        // node is ALREADY SelectedTreeNode (it exists for the re-tap case), so it silently loaded nothing and
        // the capture walked on into an empty list. A method that quietly does nothing is worse than one that
        // throws — this cost a capture run to find, and the only evidence was an empty folder list.
        //
        // The backing field rather than the property, because assigning the property fires the changed-handler,
        // which starts its OWN fire-and-forget load — two loads racing, one of them unawaitable. The adjacent
        // self-tests set this field for the same reason.
#pragma warning disable MVVMTK0034
        _selectedTreeNode = repository;
#pragma warning restore MVVMTK0034
        SetBreadcrumbFromTreeNode(repository);
        await LoadFolderContentsAsync(repository.Id, repository.Links);

        // Then DOWN through the real filing tree, one folder at a time, through the ORDINARY open path — which
        // loads the contents, resolves the ancestor breadcrumb and selects, exactly as a double-click does.
        //
        // Loading the contents directly was the first version and it rendered a figure whose breadcrumb said
        // "Demo Repository" while the list showed Acme Corp's documents: the navigation happened behind the
        // breadcrumb's back. A capture that drives a shortcut is a capture of something no user can reach.
        foreach (var folderName in LiveShotPath)
        {
            var folder = Items.FirstOrDefault(n => n.IsFolder && !n.IsReference && n.Name == folderName)
                ?? throw new InvalidOperationException(
                    $"The live app has no folder '{folderName}' where the figure expects it (walking "
                    + $"{string.Join(" / ", LiveShotPath)} under '{LiveShotRepository}'). The seeder builds this "
                    + $"tree in SeedRichTreeAsync — if it moved, update {nameof(LiveShotPath)}. Listed here: "
                    + $"{string.Join(", ", Items.Where(i => i.IsFolder).Select(i => i.Name))}.");

            await OpenFolderAsync(folder.Href("self"));
        }

        var document = Items.FirstOrDefault(n => n.Name == LiveShotDocument && !n.IsFolder)
            ?? throw new InvalidOperationException(
                $"'{LiveShotDocument}' is not in '{string.Join(" / ", LiveShotPath)}' in the live app. The figure needs a "
                + $"document selected — Rename/Delete/Download are gated on one — and this is the document the "
                + "web capture and the PR-gate fixture both name, so all three move together or not at all.");

        // The real selection path: this triggers the detail load, the index fields, the preview and the comment
        // thread, exactly as a click does. Nothing here fills those in — if they are empty in the capture, the
        // product produced nothing, which is the finding rather than a harness problem.
        SelectedItem = document;
    }

    /// <summary>
    /// Whether the panes the figure shows have finished arriving.
    /// </summary>
    /// <remarks>
    /// <b>The capture cannot simply await the selection.</b> <c>OnSelectedItemChanged</c> is a property-changed
    /// hook that fires and forgets through <c>Safe.Fire</c> (it has to: a setter cannot be awaited), so the
    /// loads it starts are not reachable as a Task. A headless capture therefore pumps until the panes are
    /// populated.
    ///
    /// <b>Asking only about the DETAIL was not enough, and the first live figure proved it.</b> The title
    /// arrives well before the preview rasterises, so the capture passed its own check and published a preview
    /// pane reading "Loading…" beside an empty chat — a figure of a spinner, which is worse than no figure
    /// because it looks like the product. Each pane the figure SHOWS is therefore named here: whatever this
    /// does not ask about is what the next published figure will be caught mid-load.
    /// </remarks>
    internal bool LiveShotPanesArrived =>
        DetailTitle == LiveShotDocument      // the detail pane is describing the right document…
        && Preview.HasPreviewPages            // …its preview has rasterised, rather than still saying "Loading…"
        && Comments.Count > 0;                // …and the thread has arrived (ADR 0545 files one for the filing)
}
