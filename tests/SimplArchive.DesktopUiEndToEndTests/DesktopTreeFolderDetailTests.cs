using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Selecting a folder in the TREE describes it in the detail pane, as selecting it as a ROW already does.
//
// Reported from live testing: "Administration/Users/Anna Current" shows no mask on the desktop while the web
// shows "User Folder". The admin branch is where it was NOTICED; the question this file answers first is
// whether it is admin-specific or general — so the ordinary-folder case is asserted too, and it is the one that
// says how big the defect really is.
//
// Issue #408 already decided the behaviour: "a folder is a Document with a mask, and it now gets the same pane
// rather than a thinner one of its own". The desktop honours that from the LIST (`SelectedItem` →
// `LoadDetailAsync`) and not from the tree, where the selection path clears the detail and never reloads it.
[Collection(UiCollection.Name)]
public class DesktopTreeFolderDetailTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopTreeFolderDetailTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Selecting_an_ORDINARY_folder_in_the_tree_describes_it()
    {
        // The scope question. If this fails too, the defect is general and the admin branch merely showed it.
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        var repository = vm.Tree.First(n => n.Id != Guid.Empty);
        vm.SelectedTreeNode = repository;

        // Waited on the DETAIL, not on Items: the folder's own detail loads AFTER its contents, so waiting for
        // rows and asserting the title is a race — it passed alone and failed the moment a sibling test changed
        // the timing. Wait for the thing being asserted.
        await WaitForAsync(() => vm.DetailTitle == repository.Name, seconds: 25);

        Assert.True(!string.IsNullOrEmpty(vm.DetailTitle),
            "selecting a folder in the tree left the detail pane empty — a folder is a Document with a mask and "
            + "gets the same pane wherever you reached it from (#408), which the LIST path already honours");
        Assert.Equal(repository.Name, vm.DetailTitle);
        Assert.True(vm.DetailIsFolder, "the pane should be describing a FOLDER");
    }

    [Fact]
    public async Task A_users_personal_space_under_Administration_shows_its_MASK()
    {
        // THE REPORTED CASE. "Administration/Users/Anna Current" showed no mask on the desktop while the web
        // showed "User Folder" — because the admin listing advertised no `mask` rel and
        // LoadMaskAndIndexAsync writes literally "No mask" when the row has none.
        //
        // The ordinary-folder case above passes and always did, which is what says this was never a general
        // tree-selection gap: it was one listing short of a rel its consumers follow.
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        var administration = vm.Tree.Single(n => n.Name == "Administration");
        await administration.EnsureExpandedAsync();
        var users = administration.Children.Single(n => n.Name == "Users");
        await users.EnsureExpandedAsync();

        var person = users.Children.First(n => n.Id != Guid.Empty);
        vm.SelectedTreeNode = person;
        await WaitForAsync(() => vm.DetailTitle == person.Name, seconds: 25);

        // The web's answer for the same folder is "User Folder"; the desktop must not say "No mask".
        await WaitForAsync(() => !string.IsNullOrEmpty(vm.MaskLine), seconds: 20);
        Assert.False(vm.MaskLine.Contains("No mask", StringComparison.OrdinalIgnoreCase),
            $"the detail pane says '{vm.MaskLine}' for a user's personal space. A personal space's root is a "
            + "Document with a mask like any other — the listing was short of the `mask` rel the pane follows, "
            + "and the desktop takes a row's links as they come rather than re-reading the resource.");
        Assert.Contains("Mask:", vm.MaskLine, StringComparison.Ordinal);
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
