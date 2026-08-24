using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// A target picker offers the same roots the tree shows (ADR 0689).
//
// It did not: the tree builds its roots from two sources — the personal space, fetched from the `me` resource,
// and GET /repositories, which deliberately EXCLUDES it — while every picker built its roots from the second
// alone. So Move, Place reference and inbox filing offered the shared repositories and silently omitted the
// user's own space, which is the one place a person is most likely to be filing into. It reads as a permission
// problem rather than a missing root, because the tree shows it the whole time.
//
// Asserted as a PARITY between the two lists rather than "the picker contains a personal root", because the
// defect was drift between two answers to one question — a test that pinned one of them would let the other
// move again.
[Collection(UiCollection.Name)]
public class DesktopFilingRootsParityTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopFilingRootsParityTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_picker_offers_the_same_roots_as_the_tree()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);

        // The tree's own roots, minus the synthetic Administration branch: that is a place to LOOK (a tenant
        // admin browsing other people's spaces), not a place to put something, so no picker offers it.
        var treeRoots = vm.Tree.Where(n => !n.IsSynthetic).Select(n => n.Name).ToList();

        var picker = vm.CreateMoveTargetPickerViewModel();
        await picker.LoadAsync();

        Assert.Equal(treeRoots, picker.Roots.Select(n => n.Name));
        Assert.Contains(picker.Roots, n => n.IsPersonal);
    }

    [Fact]
    public async Task The_personal_root_is_shown_and_expands_but_is_not_a_target()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);

        var picker = vm.CreateMoveTargetPickerViewModel();
        await picker.LoadAsync();

        var personal = picker.Roots.Single(n => n.IsPersonal);

        // Selecting it commits nothing: a personal space's first level is provisioned, not user-filled (#634),
        // so the server refuses. The File button follows CanCommit, which is asked of the same BuildResult the
        // dialog closes with — so the button and the outcome cannot disagree about what counts as a target.
        picker.SelectedNode = personal;
        Assert.False(picker.CanCommit);
        Assert.Null(picker.BuildResult());

        // A folder INSIDE it is a target — which is the whole point of showing the root.
        var inside = (await ExpandAsync(personal)).FirstOrDefault();
        Assert.NotNull(inside);
        picker.SelectedNode = inside;
        Assert.True(picker.CanCommit);

        // And an ordinary repository still is, so the refusal is specific rather than a picker that refuses
        // everything.
        picker.SelectedNode = picker.Roots.First(n => !n.IsPersonal);
        Assert.True(picker.CanCommit);
    }

    // EnsureExpandedAsync, not the IsExpanded setter: the setter's handler is fire-and-forget, so a test that
    // set it would read the placeholder child and call the personal space empty.
    private static async Task<IReadOnlyList<TreeNodeViewModel>> ExpandAsync(TreeNodeViewModel node)
    {
        await node.EnsureExpandedAsync();
        return [.. node.Children.Where(c => c.Name != "…")];
    }
}
