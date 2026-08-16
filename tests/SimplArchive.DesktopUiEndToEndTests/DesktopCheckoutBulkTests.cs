using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Check-out tab's bulk half (#521's last piece), at the view-model level against the running API: the
// selection-aware ribbon gates, and CheckInSelectionAsync / DiscardSelectionAsync composing across several
// rows with one "{ok} of {n}" summary. The single-subject verbs (edit, compare, unlock, extend) gate on a
// SINGLE selection — with several rows highlighted, a button that would act on one of them claims a scope it
// does not have.
[Collection(UiCollection.Name)]
public class DesktopCheckoutBulkTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCheckoutBulkTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Bulk_checkin_commits_every_modified_selected_row_and_gates_say_so()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var names = new[] { $"cobulk-{Guid.NewGuid():N}.txt", $"cobulk-{Guid.NewGuid():N}.txt" };
        var ids = new List<Guid>();
        foreach (var fileName in names)
        {
            await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("original"));
            var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
            ids.Add(doc.Id);
            await api.Checkout.CheckOutViaDocumentAsync(doc.Href("self"));
            await api.Checkout.SaveWorkingCopyAsync((await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == doc.Id), Encoding.UTF8.GetBytes("edited"));
        }

        var vm = new CheckoutTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();
        var rows = vm.Items.Where(i => ids.Contains(i.Id)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsModified));

        // Two rows selected: the composing verbs allow, the single-subject verbs refuse.
        vm.SetSelection(rows);
        Assert.True(vm.SelectedCanCheckIn);
        Assert.True(vm.SelectedCanDiscard);
        Assert.False(vm.HasSelectedRow);            // gates edit
        Assert.False(vm.SelectedIsSingleModified);  // gates compare
        Assert.False(vm.SelectedCanUnlock);
        Assert.False(vm.SelectedCanExtend);

        // Bulk check-in: both promoted, one summary in the "{ok} of {n}" shape.
        string? reported = null;
        vm.StatusReporter = m => reported = m;
        await vm.CheckInSelectionAsync(rows);

        Assert.Equal(string.Format(SimplArchive.Localization.Strings.Get("CoBulkCheckedIn"), 2, 2), reported);
        Assert.DoesNotContain(vm.Items, i => ids.Contains(i.Id)); // both released, so both left the tab

        // And a single-row selection flips the gates back the other way.
        var single = new CheckoutRowViewModel
        {
            Id = Guid.NewGuid(),
            Item = null,
            Name = "n",
            Path = "p",
            FileExtension = ".txt",
            IsModified = true,
            StashDownloadUrl = null,
            ExpiresAt = null,
        };
        vm.SetSelection([single]);
        Assert.True(vm.HasSelectedRow);
    }

    [Fact]
    public async Task Bulk_discard_releases_every_selected_row()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var fileName = $"codisc-{Guid.NewGuid():N}.txt";
            await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("original"));
            var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
            ids.Add(doc.Id);
            await api.Checkout.CheckOutViaDocumentAsync(doc.Href("self"));
            await api.Checkout.SaveWorkingCopyAsync((await api.Checkout.GetCheckoutsAsync()).Single(c => c.Id == doc.Id), Encoding.UTF8.GetBytes("edited"));
        }

        var vm = new CheckoutTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();
        var rows = vm.Items.Where(i => ids.Contains(i.Id)).ToList();

        string? reported = null;
        vm.StatusReporter = m => reported = m;
        await vm.DiscardSelectionAsync(rows);

        Assert.Equal(string.Format(SimplArchive.Localization.Strings.Get("CoBulkDiscarded"), 2, 2), reported);
        Assert.DoesNotContain(vm.Items, i => ids.Contains(i.Id));
    }
}
