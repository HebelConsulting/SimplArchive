using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Ctrl/Cmd+P tenant manager (ADR "Desktop tenant configuration") at the VM level — pure config logic, no
// server needed (so no fixture collection): add / edit / remove a deployment profile (name + API-root URL) and
// confirm it round-trips through the persisted tenant-config file.
[Collection("DesktopConfig")]
public class DesktopTenantManagerTests
{
    [Fact]
    public void Add_edit_remove_persist_round_trip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"tenants-{Guid.NewGuid():N}.json");
        TenantProfileStore.PathOverride = tmp;
        try
        {
            var vm = new TenantManagerViewModel();
            Assert.Empty(vm.Tenants);

            // Add a tenant.
            vm.AddCommand.Execute(null);
            Assert.True(vm.IsEditing);
            vm.EditName = "Production";
            vm.EditUrl = "https://host/simplarchive";
            vm.SaveCommand.Execute(null);
            Assert.False(vm.IsEditing);
            Assert.Single(vm.Tenants);

            // Persisted — a fresh VM loads it.
            var reloaded = new TenantManagerViewModel();
            Assert.Single(reloaded.Tenants);
            Assert.Equal("Production", reloaded.Tenants[0].Name);
            Assert.Equal("https://host/simplarchive", reloaded.Tenants[0].ApiRootUrl);

            // Edit it.
            reloaded.Selected = reloaded.Tenants[0];
            reloaded.EditCommand.Execute(null);
            reloaded.EditName = "Prod";
            reloaded.EditUrl = "https://host/sa";
            reloaded.SaveCommand.Execute(null);
            Assert.Equal("Prod", reloaded.Tenants[0].Name);
            Assert.Equal("https://host/sa", new TenantManagerViewModel().Tenants[0].ApiRootUrl);

            // An invalid URL is rejected (stays in edit mode with an error).
            reloaded.Selected = reloaded.Tenants[0];
            reloaded.EditCommand.Execute(null);
            reloaded.EditUrl = "not-a-url";
            reloaded.SaveCommand.Execute(null);
            Assert.True(reloaded.IsEditing);
            Assert.NotEmpty(reloaded.Error);
            reloaded.CancelCommand.Execute(null);
            Assert.False(reloaded.IsEditing);

            // Add a second tenant so removal is allowed (the last one can't be deleted).
            reloaded.AddCommand.Execute(null);
            reloaded.EditName = "Staging";
            reloaded.EditUrl = "https://staging/sa";
            reloaded.SaveCommand.Execute(null);
            Assert.Equal(2, reloaded.Tenants.Count);

            // Remove one → back to a single tenant, which can no longer be removed (must always have one to log into).
            reloaded.Selected = reloaded.Tenants[0];
            Assert.True(reloaded.RemoveCommand.CanExecute(null));
            reloaded.RemoveCommand.Execute(null);
            Assert.Single(reloaded.Tenants);
            Assert.False(reloaded.RemoveCommand.CanExecute(null)); // the last one can't be deleted
            reloaded.RemoveCommand.Execute(null);                  // a no-op
            Assert.Single(reloaded.Tenants);
        }
        finally
        {
            TenantProfileStore.PathOverride = null;
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
