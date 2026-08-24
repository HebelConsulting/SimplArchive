using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Opening a user's personal space from Administration → Users (ADR "Tenant-admin Administration → Users view").
//
// DesktopAdminUsersTests already covers this — through the API CLIENT, which is why the tree could crash on it
// unnoticed: the listing is fetched correctly and the row carries the right addresses, and the fault is in what
// the VIEW-MODEL does when one of those rows becomes a selected tree node.
[Collection(UiCollection.Name)]
public class DesktopAdminUserNodeTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminUserNodeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Opening_a_users_personal_space_from_the_admin_branch_lists_it()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Administration → Users → a user, each level expanded through the node's own loader.
        var administration = vm.Tree.Single(n => n.Name == "Administration");
        await administration.EnsureExpandedAsync();

        var users = administration.Children.Single(n => n.Name == "Users");
        await users.EnsureExpandedAsync();

        // EVERY user node, expanded AND selected: the rows differ from each other in ways the tree acts on —
        // an inactive user is renamed, and `take-over` is advertised on everyone except the caller.
        foreach (var candidate in users.Children.Where(n => n.Id != Guid.Empty).ToList())
        {
            await candidate.EnsureExpandedAsync();
            vm.SelectedTreeNode = candidate;
            await WaitForAsync(() => vm.CurrentFolderName == candidate.Name);
            Assert.Equal(candidate.Name, vm.CurrentFolderName);
        }

        var person = users.Children.First(n => n.Id != Guid.Empty);

        // Selecting it is what a user does to open it, and what no test did.
        vm.SelectedTreeNode = person;
        await WaitForAsync(() => vm.Breadcrumbs.Count > 0 && vm.CurrentFolderName == person.Name);

        // The listing loaded — the breadcrumb names the user's space, and the status is not a failure.
        Assert.Equal(person.Name, vm.CurrentFolderName);
        Assert.DoesNotContain("Could not", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100);
        }
    }
}
