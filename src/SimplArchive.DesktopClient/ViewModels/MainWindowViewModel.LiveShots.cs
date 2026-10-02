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

    // ---- The second slice: the four screens whose data the seed already holds (#1358) -------------------
    //
    // SIBLINGS, NOT PARAMETERS — as the workbench method above says: these screens differ by which TAB and
    // which ROW, which is navigation rather than configuration, and a single method with a switch in it would
    // be the populator shape this epic exists to retire.
    //
    // ONE RULE THEY ALL SHARE, and it is the trap the workbench already paid for. Assigning the `SelectedTab`
    // PROPERTY fires a changed-handler that starts the tab's load fire-and-forget, so the capture would race an
    // unawaitable load it cannot see. Each method therefore AWAITS the tab's own load and then sets the backing
    // field, which is the same reason the workbench assigns `_selectedTreeNode` directly.

    /// <summary>Signs in and opens Users &amp; groups with a principal selected, from the real app.</summary>
    /// <remarks>
    /// The figure's subject is a GROUP, because a group is what shows both halves of the pane — the rights
    /// matrix and the Members list. The fixture invented <c>Administrators</c>, <c>Editors</c>, <c>Jane Doe</c>
    /// and <c>Bob Smith</c>; whatever the seeder really creates is what a live capture will show, and that
    /// difference is the entire point of the migration.
    /// </remarks>
    internal async Task PopulateUsersFromLiveAppAsync(string accessToken)
    {
        await SignInForLiveShotAsync(accessToken);
        await LoadPrincipalsAsync();

        var subject = Principals.FirstOrDefault(p => p.IsGroup)
            ?? Principals.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The live app lists no principals at all, so the Users & groups figure would be an empty pane. "
                + "The seeder creates the demo users and groups — either the capture is pointed at an app that "
                + "was not seeded, or the caller cannot manage users and the tab is not even visible to them.");

        // The real selection path, which loads the photo and the members exactly as a click does. It is
        // fire-and-forget by construction (a setter cannot be awaited), which is why there is a predicate below
        // rather than an await here.
        SelectedPrincipal = subject;
        SetLiveShotTab(6);
    }

    /// <summary>Whether the Users &amp; groups figure has everything it shows.</summary>
    /// <remarks>
    /// Both panes are named, per the lesson the first live figure taught: asking only about the list would pass
    /// while the rights matrix beside it was still empty, and a figure of a half-drawn pane looks like the
    /// product rather than like a harness fault.
    /// </remarks>
    internal bool LiveShotUsersArrived =>
        Principals.Count > 0 && PrincipalRights.Count > 0 && PrincipalRightsHeader.Length > 0;

    /// <summary>Signs in and opens the Tenant tab, from the real app.</summary>
    /// <remarks>
    /// This screen is a pure read of one resource, so it has no row to select — which makes it the simplest of
    /// the four and the one most likely to drift unnoticed, since a settings pane gains a group whenever a
    /// tenant setting is added and a fixture gains one only when somebody remembers.
    /// </remarks>
    internal async Task PopulateTenantFromLiveAppAsync(string accessToken)
    {
        await SignInForLiveShotAsync(accessToken);
        await LoadTenantSettingsAsync();
        SetLiveShotTab(10);
    }

    /// <summary>Whether the Tenant figure has arrived.</summary>
    /// <remarks>
    /// <c>TenantSettingsLoaded</c> is the pane's own gate — the markup renders nothing until it is set — so
    /// asking anything narrower would be asking about a value rather than about the figure. The identity line
    /// is named beside it because it is the REFERENCE card the pane leads with, and an empty one would publish
    /// a figure of a tenant with no name.
    /// </remarks>
    internal bool LiveShotTenantArrived => TenantSettingsLoaded && TenantName.Length > 0 && TenantId.Length > 0;

    /// <summary>Signs in and opens Contacts with a contact selected, from the real app.</summary>
    internal async Task PopulateContactsFromLiveAppAsync(string accessToken)
    {
        await SignInForLiveShotAsync(accessToken);
        await ContactsTab.LoadAsync();

        // Selecting the first contact is what fills the detail half; with none selected the figure is a list
        // beside an empty pane, which is not what the manual describes.
        ContactsTab.Selected = ContactsTab.Contacts.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The live app has no contacts, so the Contacts figure would be an empty list. The seeder files "
                + "them into the demo address book — if that changed, this screen goes back to Capture.Fixture "
                + "with the reason written down rather than being published empty.");

        SetLiveShotTab(13);
    }

    /// <summary>Whether the Contacts figure has arrived.</summary>
    internal bool LiveShotContactsArrived =>
        ContactsTab.Collections.Count > 0 && ContactsTab.Contacts.Count > 0 && ContactsTab.Selected is not null;

    // CALENDAR IS NOT HERE, AND THAT IS THE FINDING. It was written, captured, and sent back to
    // Capture.Fixture the same hour: the live grid drew OCTOBER 2026 against a seed whose frozen clock is
    // 2026-06-01, because a calendar's grid comes from the CLIENT's today while Demo:Clock freezes the
    // SERVER's. The figure would have churned on the first of every month, and the harness's determinism
    // check passed it — two captures seconds apart agree about the month, which is the "determinism proves
    // stability, never correctness" lesson arriving a second time. The reason is recorded at the screen in
    // Screens.cs, where whoever picks it up next will be standing.

    /// <summary>The sign-in every live screen begins with.</summary>
    /// <remarks>
    /// Shared because it is genuinely the same act, unlike the navigation above: the token, the user context
    /// and the gated flags (<c>CanManageUsers</c>, <c>IsTenantAdmin</c>) come from the server, and three of
    /// these four tabs are not even VISIBLE without them — so a figure captured without this would be of a
    /// tab the user cannot reach.
    /// </remarks>
    private async Task SignInForLiveShotAsync(string accessToken)
    {
        UseApi(new SimplArchiveApiClient(accessToken));
        IsLoggedIn = true;
        await SetupUserContextAsync();
    }

    /// <summary>Puts a tab in front WITHOUT starting its load a second time.</summary>
    /// <remarks>
    /// The property's changed-handler fires the tab's load through <c>Safe.Fire</c>, so assigning it after the
    /// caller has already awaited that load would run it twice — once awaited and once not — and the capture
    /// would race the unawaitable copy. Setting the field puts the tab in front and starts nothing.
    /// </remarks>
    private void SetLiveShotTab(int index)
    {
#pragma warning disable MVVMTK0034
        _selectedTab = index;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedTab));
    }
}

