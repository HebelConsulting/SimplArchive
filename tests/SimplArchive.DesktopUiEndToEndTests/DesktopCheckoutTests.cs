using System.Text;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop check-out / check-in (ADR "Document check-out / check-in") through the real SimplArchiveApiClient +
// CheckoutTabViewModel against the running API: check out a document, download the working copy, see the tab
// report Unchanged → Modified as the local file changes, then check in (upload a new version + release).
[Collection(UiCollection.Name)]
public class DesktopCheckoutTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopCheckoutTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Checkout_download_modify_and_checkin_through_the_tab()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        // Upload a fresh document (a document with a confirmed version) into the demo repository.
        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"co-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("original content"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));

        // Check it out → the child listing now reports it as checked out by me.
        await api.CheckOutAsync(doc.Id);
        var afterCheckout = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Id == doc.Id);
        Assert.True(afterCheckout.CheckedOut);
        Assert.True(afterCheckout.CheckedOutByMe);

        // Drive the Check-out tab against an isolated local folder.
        var tenant = $"co-tenant-{Guid.NewGuid():N}";
        var folders = new LocalFolders(tenant, "user");
        try
        {
            var vm = new CheckoutTabViewModel();
            vm.Setup(api, folders);

            // Download the working copy into the local checkout folder → the tab reports it Unchanged.
            await vm.DownloadWorkingCopyAsync(doc.Id, doc.Name, ".txt");
            await vm.LoadAsync();
            var row = vm.Items.Single(i => i.Id == doc.Id);
            Assert.False(row.IsModified);
            Assert.True(row.CanUnlock);

            // Edit the local working copy → the tab now reports it Modified (offers Check in / Discard).
            await File.WriteAllTextAsync(row.LocalPath, "edited locally");
            await vm.LoadAsync();
            row = vm.Items.Single(i => i.Id == doc.Id);
            Assert.True(row.IsModified);
            Assert.True(row.CanCheckIn);

            // Check in → uploads the edited file as a new version, releases the lock, clears the working copy.
            await vm.CheckInCommand.ExecuteAsync(row);
            Assert.DoesNotContain(vm.Items, i => i.Id == doc.Id);
            Assert.False(File.Exists(row.LocalPath));

            // The document is free again, and its latest version is the edited content.
            Assert.False((await api.GetChildrenAsync(repo.Id)).Single(n => n.Id == doc.Id).CheckedOut);
            var bytes = await api.DownloadCurrentVersionAsync(doc.Id);
            Assert.Equal("edited locally", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            var tenantRoot = Directory.GetParent(folders.Root)!.FullName;
            if (Directory.Exists(tenantRoot))
            {
                Directory.Delete(tenantRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Extend_keeps_the_lock_through_the_api_client()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"ext-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("v1"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.CheckOutAsync(doc.Id);

        // Extend (self-service, ADR "Self-service check-out extension") — no throw, and the lock is retained.
        await api.ExtendCheckoutAsync(doc.Id);
        Assert.Contains(await api.GetCheckoutsAsync(), c => c.Id == doc.Id);

        // Clean up: release the lock.
        await api.CheckInAsync(doc.Id);
    }

    [Fact]
    public async Task Save_to_cloud_stashes_login_reconcile_restores_and_conflict_keeps_local()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"st-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("original"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.CheckOutAsync(doc.Id);

        var t1 = $"st-a-{Guid.NewGuid():N}";
        var t2 = $"st-b-{Guid.NewGuid():N}";
        var foldersA = new LocalFolders(t1, "user");
        var foldersB = new LocalFolders(t2, "user");
        try
        {
            // Session A: download the working copy, edit it locally, then Save to cloud (stash it).
            var vmA = new CheckoutTabViewModel();
            vmA.Setup(api, foldersA);
            await vmA.DownloadWorkingCopyAsync(doc.Id, doc.Name, ".txt");
            await vmA.LoadAsync();
            var rowA = vmA.Items.Single(i => i.Id == doc.Id);
            await File.WriteAllTextAsync(rowA.LocalPath, "edited work in progress");
            await vmA.LoadAsync();
            rowA = vmA.Items.Single(i => i.Id == doc.Id);
            Assert.True(rowA.IsUnsynced);          // un-saved edits
            Assert.True(rowA.CanSaveToCloud);

            await vmA.SaveToCloudCommand.ExecuteAsync(rowA);
            Assert.True((await api.GetCheckoutsAsync()).Single(i => i.Id == doc.Id).HasStash); // stash now exists
            await vmA.LoadAsync();
            rowA = vmA.Items.Single(i => i.Id == doc.Id);
            Assert.False(rowA.IsUnsynced);         // backed up to the cloud
            Assert.True(File.Exists(rowA.LocalPath)); // local file kept (Save to cloud isn't "done")

            // Session B (a fresh login, empty local folder): reconcile restores the stashed working copy.
            var vmB = new CheckoutTabViewModel();
            vmB.Setup(api, foldersB);
            await vmB.ReconcileOnLoginAsync();
            var rowB = vmB.Items.Single(i => i.Id == doc.Id);
            Assert.Equal("edited work in progress", await File.ReadAllTextAsync(rowB.LocalPath));
            Assert.False(rowB.IsUnsynced);

            // Conflict: a divergent local file already present at login is KEPT (never overwritten) + flagged.
            await File.WriteAllTextAsync(rowB.LocalPath, "different local edits from a crash");
            await vmB.ReconcileOnLoginAsync();
            rowB = vmB.Items.Single(i => i.Id == doc.Id);
            Assert.Equal("different local edits from a crash", await File.ReadAllTextAsync(rowB.LocalPath)); // not overwritten
            Assert.True(rowB.IsUnsynced);          // surfaced as un-synced for the user to resolve

            // Clean up: discard releases the lock (which clears the stash server-side).
            await vmB.DiscardAsync(rowB);
            Assert.DoesNotContain(vmB.Items, i => i.Id == doc.Id);
            Assert.DoesNotContain(await api.GetCheckoutsAsync(), i => i.Id == doc.Id);
        }
        finally
        {
            foreach (var root in new[] { foldersA.Root, foldersB.Root })
            {
                var tenantRoot = Directory.GetParent(root)!.FullName;
                if (Directory.Exists(tenantRoot))
                {
                    Directory.Delete(tenantRoot, recursive: true);
                }
            }
        }
    }

    [Fact]
    public async Task Orphaned_local_copy_after_release_elsewhere_can_be_added_as_a_new_version()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var fileName = $"orph-{Guid.NewGuid():N}.txt";
        await api.UploadFileAsync(repo.Id, fileName, Encoding.UTF8.GetBytes("v1"));
        var doc = (await api.GetChildrenAsync(repo.Id)).Single(n => n.Name == Path.GetFileNameWithoutExtension(fileName));
        await api.CheckOutAsync(doc.Id);

        var tenant = $"orph-{Guid.NewGuid():N}";
        var folders = new LocalFolders(tenant, "user");
        try
        {
            var vm = new CheckoutTabViewModel();
            vm.Setup(api, folders);
            await vm.DownloadWorkingCopyAsync(doc.Id, doc.Name, ".txt");
            await vm.LoadAsync();
            var row = vm.Items.Single(i => i.Id == doc.Id);
            await File.WriteAllTextAsync(row.LocalPath, "my local edits");

            // The hidden bookkeeping manifest is written (a dotfile that never surfaces in a file view).
            Assert.True(File.Exists(Path.Combine(folders.CheckoutDirectory, LocalFolders.CheckoutManifestFileName)));

            // The check-out is released ELSEWHERE (another client / the web / an override) — here, a direct
            // release outside this VM, so the VM's manifest entry + local file remain.
            await api.CheckInAsync(doc.Id);

            // On the next reconcile, the local copy is detected as orphaned (released, but edits remain).
            await vm.ReconcileOnLoginAsync();
            Assert.True(vm.HasOrphans);
            var orphan = vm.Orphans.Single(o => o.Id == doc.Id);

            // Add as new version: the local edits are committed as a new version of the now-unlocked document.
            await vm.AddOrphanAsVersionCommand.ExecuteAsync(orphan);
            Assert.False(vm.HasOrphans);
            Assert.False(File.Exists(row.LocalPath));           // local working copy cleared
            var bytes = await api.DownloadCurrentVersionAsync(doc.Id);
            Assert.Equal("my local edits", Encoding.UTF8.GetString(bytes)); // committed as the latest version
        }
        finally
        {
            var tenantRoot = Directory.GetParent(folders.Root)!.FullName;
            if (Directory.Exists(tenantRoot))
            {
                Directory.Delete(tenantRoot, recursive: true);
            }
        }
    }
}
