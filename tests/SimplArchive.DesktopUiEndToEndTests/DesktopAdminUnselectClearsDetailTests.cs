using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Unselecting a row under Administration → Users leaves the detail pane describing NOTHING, not the row just
// deselected.
//
// Reported from live testing: select a user in the list, then unselect (click empty space, or cmd-click), and
// the pane still shows that user's mask. ADR 0559's rule is that a pane with nothing to show shows nothing —
// never the previous subject's values, which is a claim about the wrong object and looks like a fact.
//
// The fallback on unselect is "describe the folder you are standing in". Under Administration there IS no
// folder — the node is synthetic — so the honest answer is an empty pane.
[Collection(UiCollection.Name)]
public class DesktopAdminUnselectClearsDetailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminUnselectClearsDetailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Unselecting_a_user_row_does_not_leave_its_mask_standing()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Stand in a REAL folder first, so `_currentFolderId` holds something — the state a user actually
        // arrives with, and the one that makes a stale fallback possible.
        var repository = vm.Tree.First(n => n.Id != Guid.Empty);
        vm.SelectedTreeNode = repository;
        await WaitForAsync(() => vm.DetailTitle == repository.Name, seconds: 25);

        var administration = vm.Tree.Single(n => n.Name == "Administration");
        await administration.EnsureExpandedAsync();
        var users = administration.Children.Single(n => n.Name == "Users");

        vm.SelectedTreeNode = users;
        await WaitForAsync(() => vm.Items.Any(i => i.Id != Guid.Empty), seconds: 20);

        var person = vm.Items.First(i => i.Id != Guid.Empty);
        vm.SelectedItem = person;

        // Waited on MASKLINE, not DetailTitle: the title is set at the top of the detail load and the mask
        // arrives from a later fetch, so waiting for the title and asserting the mask is a race — the same one
        // that made the sibling folder-detail test pass alone and fail beside a neighbour.
        vm.SelectedItem = person;
        await WaitForAsync(() => vm.MaskLine.Contains("Mask:", StringComparison.Ordinal), seconds: 20);
        Assert.Contains("Mask:", vm.MaskLine, StringComparison.Ordinal);   // precondition: it IS describing them

        // The unselect.
        vm.SelectedItem = null;
        await Task.Delay(1200);

        Assert.True(string.IsNullOrEmpty(vm.DetailTitle),
            $"after unselecting, the pane still describes '{vm.DetailTitle}' — the previous subject (ADR 0559)");
        Assert.True(string.IsNullOrEmpty(vm.MaskLine),
            $"after unselecting, the pane still shows '{vm.MaskLine}' — the mask of the row just deselected");
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {seconds}s waiting for the condition.");
    }
}
