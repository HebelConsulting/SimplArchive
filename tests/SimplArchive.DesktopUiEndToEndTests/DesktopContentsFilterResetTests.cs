using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// A column filter is cleared by NAVIGATION and survives a same-folder reload (#1275).
//
// WHAT WAS WRONG. The desktop never cleared the four column filters — not on navigation, not on returning to
// the roots, not on LOGOUT — so a filter typed in one folder kept narrowing the next one's rows. The symptom
// is silent: a filtered list looks exactly like a short one, and the hint that would explain it
// (`ContentsFilterActive`) is documented as existing for that purpose but is not bound in any view.
//
// WHY ONLY THE DESKTOP. The web client has always cleared them, and has a test saying so
// (`WebContentsFilterTests.Column_filters_narrow_the_rows_and_reset_on_folder_change`). The desktop's sibling
// test covered the projection and stopped short of the reset — so this is a parity defect that existed
// precisely in the shape of the missing test.
//
// THE ASSERTION THAT MATTERS is that the hidden row comes BACK. Checking only that the filter field is empty
// passes against a build that clears the text while leaving the predicate applied — which is worse than
// either state, and is the pair the web client's own `StateHasChanged` comment records coming apart.
[Collection(UiCollection.Name)]
public class DesktopContentsFilterResetTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopContentsFilterResetTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Navigating_to_another_folder_clears_the_filter_and_brings_the_hidden_rows_back()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        await WaitForAsync(() => vm.Tree.Count > 0);

        // Two real repositories, so the navigation is a genuine folder change rather than a reload.
        var repositories = vm.Tree.Where(n => n.Id != Guid.Empty).Take(2).ToList();
        Assert.Equal(2, repositories.Count);

        vm.SelectedTreeNode = repositories[0];
        await WaitForAsync(() => vm.Items.Count > 0, seconds: 25);

        // A term that matches nothing here — so the list is demonstrably narrowed to empty, and "the rows came
        // back" is unambiguous rather than a count that might have coincided.
        vm.ContentsFilterName = "zzz-no-such-row-zzz";
        Assert.Empty(vm.VisibleItems);
        Assert.True(vm.ContentsFilterActive);

        vm.SelectedTreeNode = repositories[1];
        await WaitForAsync(() => vm.Items.Count > 0 && !vm.ContentsFilterActive, seconds: 25);

        // Both halves. The field is empty...
        Assert.Equal(string.Empty, vm.ContentsFilterName);

        // ...AND the projection shows the rows again, which is the half a cleared-text-only fix would fail.
        Assert.NotEmpty(vm.VisibleItems);
        Assert.Equal(vm.Items.Count, vm.VisibleItems.Count);
    }

    [Fact]
    public void Logging_out_clears_the_filter_so_it_cannot_narrow_the_next_users_list()
    {
        // No session needed: Logout's job here is to drop loaded state, and the filter was simply not among
        // the things it dropped. Worth its own case because a filter surviving a USER SWITCH is worse than one
        // surviving a folder change — the next person never typed it and has no reason to look for it.
        var vm = new MainWindowViewModel();
        vm.ContentsFilterName = "invoice";
        vm.ContentsFilterOwner = "anna";
        Assert.True(vm.ContentsFilterActive);

        vm.LogoutCommand.Execute(null);

        Assert.False(vm.ContentsFilterActive);
    }

    [Fact]
    public void The_hint_reports_how_much_of_the_folder_is_on_screen()
    {
        // The hint is what makes a narrowed list distinguishable from a short one. It was DECLARED for exactly
        // that ("where did my rows go?") and bound in no view, so the answer existed and was never shown.
        var vm = new MainWindowViewModel();
        foreach (var name in new[] { "Invoice March", "Invoice May", "Offer April" })
        {
            vm.Items.Add(new NodeViewModel { Id = Guid.NewGuid(), Name = name, HasChildren = false, HasVersions = true, DocumentType = "Basic Entry", CreatedBy = "Demo Admin", Tags = [] });
        }

        Assert.False(vm.ContentsFilterActive);

        vm.ContentsFilterName = "invoice";
        Assert.True(vm.ContentsFilterActive);
        Assert.Contains("2", vm.ContentsFilterActiveHint);
        Assert.Contains("3", vm.ContentsFilterActiveHint);

        // Narrowed to NOTHING is the case the hint exists for — an empty list and an empty folder look the
        // same, and only the counts separate them.
        vm.ContentsFilterName = "zzz-nothing-zzz";
        Assert.Empty(vm.VisibleItems);
        Assert.Contains("0", vm.ContentsFilterActiveHint);

        // A filter that hides NOTHING still counts as applied. The projection is unchanged, so the rebuild
        // returns early — and if the notification rode with the rows rather than with the filter text, the
        // hint would stay hidden here while a filter was plainly in force.
        vm.ContentsFilterName = string.Empty;
        vm.ContentsFilterOwner = "demo admin";
        Assert.True(vm.ContentsFilterActive);
        Assert.Equal(3, vm.VisibleItems.Count);
        Assert.Contains("3", vm.ContentsFilterActiveHint);
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

        Assert.True(condition(), "condition not met within the deadline");
    }
}
