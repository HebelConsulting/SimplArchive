using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop check-out / check-in (ADR "Document check-out / check-in"; ADR 0513) through the real SimplArchiveApiClient
// + CheckoutTabViewModel against the running API. Editing goes through the WebDAV mount → the cloud stash, so the tab
// reports Unchanged → Modified from the SERVER's IsModified (SHA of the stash vs the version), and Check-in is the
// stash-based server promotion — there is no local working copy any more.
[Collection(UiCollection.Name)]
public class DesktopCheckoutTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCheckoutTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Checkout_stash_edit_reports_modified_and_stash_checkin()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"co-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("original content"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));

        await api.CheckOutAsync(doc.Id);

        var vm = new CheckoutTabViewModel();
        vm.Setup(api);

        // No stash yet → Unchanged; the row shows the file WITH its extension (ADR 0513).
        await vm.LoadAsync();
        var row = vm.Items.Single(i => i.Id == doc.Id);
        Assert.False(row.IsModified);
        Assert.True(row.CanUnlock);
        Assert.False(row.CanCheckIn);
        Assert.EndsWith(".txt", row.DisplayName);

        // Edit via the cloud stash (what a WebDAV save does) → the tab reports Modified + offers Check in.
        await api.SaveWorkingCopyAsync((await api.GetCheckoutsAsync()).Single(c => c.Id == doc.Id), Encoding.UTF8.GetBytes("edited via webdav"));
        await vm.LoadAsync();
        row = vm.Items.Single(i => i.Id == doc.Id);
        Assert.True(row.IsModified);
        Assert.True(row.CanCheckIn);
        Assert.False(row.CanUnlock);

        // Stash-based check-in → the server promotes the stash to a new version + releases the lock.
        await vm.CheckInCommand.ExecuteAsync(row);
        Assert.DoesNotContain(vm.Items, i => i.Id == doc.Id);
        Assert.False((await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Id == doc.Id).CheckedOut);
        var bytes = await api.DownloadCurrentVersionAsync(doc.Id);
        Assert.Equal("edited via webdav", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Compare_loads_the_unified_diff_of_the_working_copy_vs_the_current_version()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"cmp-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("line one\nline two\nline three\n"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));

        await api.CheckOutAsync(doc.Id);
        await api.SaveWorkingCopyAsync((await api.GetCheckoutsAsync()).Single(c => c.Id == doc.Id), Encoding.UTF8.GetBytes("line one\nline two CHANGED\nline three\n"));

        // Load the row (it carries StashDownloadUrl) and drive the compare VM exactly as the dialog does.
        var tab = new CheckoutTabViewModel();
        tab.Setup(api);
        await tab.LoadAsync();
        var row = tab.Items.Single(i => i.Id == doc.Id);
        Assert.True(row.IsModified);

        var vm = new CompareCheckoutViewModel();
        await vm.SetupAsync(api, row.Item!, row.DisplayName, row.FileExtension, row.StashDownloadUrl);

        Assert.False(vm.NotAvailable);
        Assert.Contains(vm.Lines, l => l.Op == 2 && l.Display.Contains("line two"));  // removed
        Assert.Contains(vm.Lines, l => l.Op == 1 && l.Display.Contains("CHANGED"));   // added
    }

    [Fact]
    public async Task Unchanged_checkout_unlocks_without_a_new_version()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"un-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("v1"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.CheckOutAsync(doc.Id);

        var vm = new CheckoutTabViewModel();
        vm.Setup(api);
        await vm.LoadAsync();
        var row = vm.Items.Single(i => i.Id == doc.Id);
        Assert.False(row.IsModified);

        // Unlock releases the lock without creating a version.
        await vm.UnlockCommand.ExecuteAsync(row);
        Assert.DoesNotContain(vm.Items, i => i.Id == doc.Id);
        Assert.DoesNotContain(await api.GetCheckoutsAsync(), c => c.Id == doc.Id);
    }

    [Fact]
    public async Task Extend_keeps_the_lock_through_the_api_client()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"ext-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("v1"));
        var doc = (await api.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.CheckOutAsync(doc.Id);

        // Extend (self-service, ADR "Self-service check-out extension") — no throw, and the lock is retained.
        await api.ExtendCheckoutAsync((await api.GetCheckoutsAsync()).Single(c => c.Id == doc.Id));
        Assert.Contains(await api.GetCheckoutsAsync(), c => c.Id == doc.Id);

        // Clean up: release the lock.
        await api.CheckInAsync(doc.Id);
    }
}
