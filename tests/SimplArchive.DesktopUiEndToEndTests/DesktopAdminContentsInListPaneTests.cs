using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Selecting a synthetic Administration node LISTS its contents, so the column filter can find a user (#1160).
//
// WHAT WAS WRONG. Administration and Administration/Users are synthetic tree nodes — no document id, no
// `children` rel — so the selection path early-returned on them: clicking expanded the TREE and left the list
// pane showing whatever folder was there before. With many users, the only way to reach one was to scroll the
// tree, while a perfectly good column filter sat unused one pane to the right.
//
// WHY THE ASSERTIONS ARE WHAT THEY ARE. A row is only useful here if the FILTER can match it, so the test
// asserts the two fields a person would actually type — the display name and the e-mail — rather than merely
// that some rows arrived. And it asserts the row still carries its real repository id, because that is what
// makes opening one browse into the user's space exactly as the tree node does.
[Collection(UiCollection.Name)]
public class DesktopAdminContentsInListPaneTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopAdminContentsInListPaneTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Administration_lists_Users_and_Users_lists_every_user_with_a_filterable_email()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        var administration = vm.Tree.Single(n => n.Name == "Administration");

        // 1. Administration lists its single Users entry — previously the pane kept the previous folder's rows.
        vm.SelectedTreeNode = administration;
        await WaitForAsync(() => vm.Items.Count > 0);
        Assert.Equal("Users", Assert.Single(vm.Items).Name);

        // 2. Users lists one row per user, each carrying the e-mail the filter searches on.
        await administration.EnsureExpandedAsync();
        var users = administration.Children.Single(n => n.Name == "Users");

        vm.SelectedTreeNode = users;
        await WaitForAsync(() => vm.Items.Count > 0 && vm.Items.Any(i => i.Id != Guid.Empty));

        var rows = vm.Items.Where(i => i.Id != Guid.Empty).ToList();
        Assert.NotEmpty(rows);

        // The admin themself is always present, and is the row whose values this test can name exactly.
        var admin = Assert.Single(rows, r => string.Equals(r.CreatedBy, SelfHostedAppFixture.AdminEmail, StringComparison.OrdinalIgnoreCase));

        // A REAL repository id, not the synthetic Guid.Empty the tree's grouping nodes carry — this is what
        // makes opening the row browse into that user's personal space rather than dead-end.
        Assert.NotEqual(Guid.Empty, admin.Id);
        Assert.False(string.IsNullOrWhiteSpace(admin.Name));

        // 3. The TREE and the LIST describe the same thing. Two surfaces reading one source is the point — a
        // second fetch shaped like the first is what lets them drift into disagreeing about who exists.
        await users.EnsureExpandedAsync();
        var treeIds = users.Children.Where(n => n.Id != Guid.Empty).Select(n => n.Id).OrderBy(id => id).ToList();
        var listIds = rows.Select(r => r.Id).OrderBy(id => id).ToList();
        Assert.Equal(treeIds, listIds);

        // And the same names, including the "(inactive)" spelling — if the two rendered that differently, a
        // filter typed from what the tree shows would fail to match the row beside it.
        var treeNames = users.Children.Where(n => n.Id != Guid.Empty).Select(n => n.Name).OrderBy(n => n).ToList();
        var listNames = rows.Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(treeNames, listNames);
    }

    [Fact]
    public async Task An_admin_node_leaves_the_detail_pane_EMPTY()
    {
        // An admin node is not a document, so there is no subject to describe. The rule this pins is ADR 0559's:
        // a pane that has nothing to show shows NOTHING — never the previously-selected document's values,
        // which would be a claim about the wrong object and is invisible unless asserted.
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Stand in a real folder first and select something, so the detail pane HAS a subject to leak.
        var repository = vm.Tree.First(n => n.Id != Guid.Empty);
        vm.SelectedTreeNode = repository;
        await WaitForAsync(() => vm.Items.Count > 0, seconds: 20);

        vm.SelectedItem = vm.Items.FirstOrDefault();
        await Task.Delay(500);

        vm.SelectedTreeNode = vm.Tree.Single(n => n.Name == "Administration");
        await WaitForAsync(() => vm.Items.Count == 1 && vm.Items[0].Name == "Users");

        Assert.True(string.IsNullOrEmpty(vm.DetailTitle),
            $"the detail pane still describes '{vm.DetailTitle}' after moving to an Administration node, which "
            + "is a claim about the previously-selected document (ADR 0559)");
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
