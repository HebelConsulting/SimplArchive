using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Desktop Recycle bin tab (ADR "Desktop recycle bin parity"): the tenant-wide list + restore + permanent purge,
// driven through the real RecycleBinTabViewModel + SimplArchiveApiClient against the running Api.
[Collection(UiCollection.Name)]
public class DesktopRecycleBinTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopRecycleBinTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Lists_a_deleted_item_then_restores_and_purges_it()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var name = "rbdesk-" + Guid.NewGuid().ToString("N")[..8];

        // Create a throwaway folder in the demo repository, then soft-delete it.
        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        await api.Documents.CreateFolderAsync(repo.Id, name);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Single(n => n.Name == name);
        await api.Documents.DeleteAsync(folder.Id);

        // The tenant-wide recycle-bin list shows it, with a full path and an audit-derived "deleted by".
        var vm = new RecycleBinTabViewModel();
        vm.SetApi(api);
        await vm.LoadAsync();
        var row = vm.Items.SingleOrDefault(i => i.Id == folder.Id);
        Assert.NotNull(row);
        Assert.Equal(name, row!.Name);
        Assert.Contains("Demo Repository", row.Path);
        Assert.NotEqual("—", row.DeletedBy); // resolved from the audit trail

        // Restore via the tab's command → it leaves the recycle bin.
        await vm.RestoreCommand.ExecuteAsync(row);
        await vm.LoadAsync();
        Assert.DoesNotContain(vm.Items, i => i.Id == folder.Id);
        Assert.Contains(await api.Documents.GetChildrenAsync(repo.Href("children")), n => n.Id == folder.Id); // back in the repo

        // Delete again, then permanently hard-delete it from the recycle bin.
        await api.Documents.DeleteAsync(folder.Id);
        await vm.LoadAsync();
        var again = vm.Items.Single(i => i.Id == folder.Id);
        await vm.HardDeleteCommand.ExecuteAsync(again);
        await vm.LoadAsync();
        Assert.DoesNotContain(vm.Items, i => i.Id == folder.Id); // gone for good
    }

    [Fact]
    public async Task Bulk_restore_brings_back_checked_rows()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var tag = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var names = new[] { $"bulk-a-{tag}", $"bulk-b-{tag}", $"bulk-c-{tag}" };
        foreach (var n in names) await api.Documents.CreateFolderAsync(repo.Id, n);
        var folders = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Where(n => names.Contains(n.Name)).ToList();
        foreach (var f in folders) await api.Documents.DeleteAsync(f.Id);

        var vm = new RecycleBinTabViewModel();
        vm.SetApi(api);
        await vm.LoadAsync();

        // Select A + B (leave C) — the native multi-selection, pushed the way the view pushes it (#530) —
        // then Restore selected → 2 restored.
        vm.SetSelection(vm.Items.Where(i => i.Name == names[0] || i.Name == names[1]).ToList());
        Assert.Equal(2, vm.CheckedCount);
        await vm.RestoreSelectedCommand.ExecuteAsync(null);
        await vm.LoadAsync();

        var back = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Select(n => n.Name).ToHashSet();
        Assert.Contains(names[0], back);
        Assert.Contains(names[1], back);
        Assert.DoesNotContain(names[2], back); // C still deleted
        Assert.Contains(vm.Items, i => i.Name == names[2]);
    }

    [Fact]
    public async Task Bulk_purge_permanently_removes_checked_rows()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var tag = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(n => n.Name == "Demo Repository");
        var names = new[] { $"purge-a-{tag}", $"purge-b-{tag}" };
        foreach (var n in names) await api.Documents.CreateFolderAsync(repo.Id, n);
        var folders = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Where(n => names.Contains(n.Name)).ToList();
        foreach (var f in folders) await api.Documents.DeleteAsync(f.Id);

        var vm = new RecycleBinTabViewModel { IsTenantAdmin = true };
        vm.SetApi(api);
        await vm.LoadAsync();
        vm.SetSelection(vm.Items.Where(i => names.Contains(i.Name)).ToList());
        Assert.True(vm.CanPurgeSelected);

        // Purge selected (the code-behind's "I AGREE" gate is bypassed in the test — call the VM method directly).
        await vm.PurgeSelectedAsync();
        await vm.LoadAsync();

        Assert.DoesNotContain(vm.Items, i => names.Contains(i.Name)); // gone from the bin
        var ids = folders.Select(f => f.Id).ToHashSet();
        // The rows are permanently gone — restoring them is no longer possible (not in the recycle bin).
        Assert.DoesNotContain(await api.Documents.GetChildrenAsync(repo.Href("children")), n => ids.Contains(n.Id));
    }
}
