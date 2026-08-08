using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Ctrl/Cmd+P server manager (ADR "Desktop server configuration") at the VM level — pure config logic, no
// server needed (so no fixture collection): add / edit / remove a deployment profile (name + API-root URL) and
// confirm it round-trips through the persisted server-config file.
[Collection("DesktopConfig")]
public class DesktopServerManagerTests
{
    [Fact]
    public void Add_edit_remove_persist_round_trip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"servers-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        try
        {
            var vm = new ServerManagerViewModel();
            Assert.Empty(vm.Servers);

            // Add a server.
            vm.AddCommand.Execute(null);
            Assert.True(vm.IsEditing);
            vm.EditName = "Production";
            vm.EditUrl = "https://host/simplarchive";
            vm.SaveCommand.Execute(null);
            Assert.False(vm.IsEditing);
            Assert.Single(vm.Servers);

            // Persisted — a fresh VM loads it.
            var reloaded = new ServerManagerViewModel();
            Assert.Single(reloaded.Servers);
            Assert.Equal("Production", reloaded.Servers[0].Name);
            Assert.Equal("https://host/simplarchive", reloaded.Servers[0].ApiRootUrl);

            // Edit it.
            reloaded.Selected = reloaded.Servers[0];
            reloaded.EditCommand.Execute(null);
            reloaded.EditName = "Prod";
            reloaded.EditUrl = "https://host/sa";
            reloaded.SaveCommand.Execute(null);
            Assert.Equal("Prod", reloaded.Servers[0].Name);
            Assert.Equal("https://host/sa", new ServerManagerViewModel().Servers[0].ApiRootUrl);

            // An invalid URL is rejected (stays in edit mode with an error).
            reloaded.Selected = reloaded.Servers[0];
            reloaded.EditCommand.Execute(null);
            reloaded.EditUrl = "not-a-url";
            reloaded.SaveCommand.Execute(null);
            Assert.True(reloaded.IsEditing);
            Assert.NotEmpty(reloaded.Error);
            reloaded.CancelCommand.Execute(null);
            Assert.False(reloaded.IsEditing);

            // Add a second server so removal is allowed (the last one can't be deleted).
            reloaded.AddCommand.Execute(null);
            reloaded.EditName = "Staging";
            reloaded.EditUrl = "https://staging/sa";
            reloaded.SaveCommand.Execute(null);
            Assert.Equal(2, reloaded.Servers.Count);

            // Remove one → back to a single server, which can no longer be removed (must always have one to log into).
            reloaded.Selected = reloaded.Servers[0];
            Assert.True(reloaded.RemoveCommand.CanExecute(null));
            reloaded.RemoveCommand.Execute(null);
            Assert.Single(reloaded.Servers);
            Assert.False(reloaded.RemoveCommand.CanExecute(null)); // the last one can't be deleted
            reloaded.RemoveCommand.Execute(null);                  // a no-op
            Assert.Single(reloaded.Servers);
        }
        finally
        {
            ServerProfileStore.PathOverride = null;
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    // The live URL validation (issue #270): a well-formed URL that the probe confirms is our server tints green;
    // a foreign or malformed URL stays neutral.
    [Fact]
    public async Task Url_probe_tints_green_only_for_our_server()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"servers-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        try
        {
            var vm = new ServerManagerViewModel { ProbeDebounce = TimeSpan.Zero };
            vm.ServerIdentityCheck = (url, _) => Task.FromResult(url.Contains("good"));

            vm.EditUrl = "https://good.example.com";
            await vm.ProbeEditUrlAsync();
            Assert.True(vm.EditUrlIsOurServer); // reachable + our server → green

            vm.EditUrl = "https://foreign.example.com";
            Assert.False(vm.EditUrlIsOurServer); // an edit clears the previous positive result immediately
            await vm.ProbeEditUrlAsync();
            Assert.False(vm.EditUrlIsOurServer); // probed, not our server → neutral

            // A malformed URL never probes → stays neutral even if the (unused) probe would say yes.
            vm.ServerIdentityCheck = (_, _) => Task.FromResult(true);
            vm.EditUrl = "not-a-url";
            await vm.ProbeEditUrlAsync();
            Assert.False(vm.EditUrlIsOurServer);

            // The same cue applies to a merely-selected profile (read-only pane, no edit mode) — issue #270.
            vm.ServerIdentityCheck = (url, _) => Task.FromResult(url.Contains("good"));
            vm.Servers.Add(new ServerProfile { Name = "Good", ApiRootUrl = "https://good.example.com" });
            vm.Servers.Add(new ServerProfile { Name = "Other", ApiRootUrl = "https://other.example.com" });

            vm.Selected = vm.Servers.First(t => t.Name == "Good");
            await vm.ProbeSelectedAsync();
            Assert.True(vm.SelectedIsOurServer);

            vm.Selected = vm.Servers.First(t => t.Name == "Other");
            Assert.False(vm.SelectedIsOurServer); // a selection change clears the previous tint immediately
            await vm.ProbeSelectedAsync();
            Assert.False(vm.SelectedIsOurServer);
        }
        finally
        {
            ServerProfileStore.PathOverride = null;
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
