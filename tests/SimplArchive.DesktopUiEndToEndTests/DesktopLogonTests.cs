using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.UiEndToEndTests;

// The startup logon window VM (ADR "Desktop logon window", login redesign slice B) — pure logic, no server or
// browser (the reachability + OAuth steps are injectable seams): auto-seed a default server, show the "no
// connection" message when the server is unreachable, and fire LoginSucceeded + remember the choices on success.
[Collection("DesktopConfig")]
public class DesktopLogonTests
{
    [Fact]
    public async Task Auto_seeds_shows_no_connection_and_logs_in()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"logon-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        DesktopClientOptions.ApiBaseUrl = "http://localhost:8080";
        try
        {
            var vm = new LogonViewModel();

            // Auto-seeded the public Demo deployment (the default) plus a Local one; English selected (issue #269).
            Assert.Equal(2, vm.Servers.Count);
            Assert.Equal("demo.simplarchive.dev", vm.Servers[0].Name);
            Assert.Equal("https://demo.simplarchive.dev", vm.Servers[0].ApiRootUrl);
            Assert.Equal("Local", vm.Servers[1].Name);
            Assert.NotNull(vm.SelectedServer);
            Assert.Equal("demo.simplarchive.dev", vm.SelectedServer!.Name); // Demo is the first-run default
            Assert.Equal("en", vm.SelectedLanguage!.Code);

            // Server unreachable → the "No connection" message, and no login.
            var fired = false;
            vm.LoginSucceeded += (_, _) => fired = true;
            vm.ReachabilityCheck = (_, _) => Task.FromResult(false);
            await ((IAsyncRelayCommand)vm.LoginCommand).ExecuteAsync(null);
            Assert.Contains("No connection", vm.Status);
            Assert.False(fired);
            Assert.False(vm.Busy);

            // Reachable + a token → LoginSucceeded fires with the api client + email; the choices persist.
            vm.SelectedServer = vm.Servers.First(t => t.Name == "Local");
            vm.Username = "user@example.com";
            vm.SelectedLanguage = vm.Languages.First(l => l.Code == "de");
            vm.ReachabilityCheck = (_, _) => Task.FromResult(true);
            string? passedHint = null;
            vm.Authenticate = (hint, _) =>
            {
                passedHint = hint;
                return Task.FromResult<OidcLoopbackAuthenticator.AuthResult?>(
                    new OidcLoopbackAuthenticator.AuthResult("token", "user@example.com"));
            };
            SimplArchiveApiClient? gotApi = null;
            string? gotEmail = null;
            vm.LoginSucceeded += (api, email) => { gotApi = api; gotEmail = email; };
            await ((IAsyncRelayCommand)vm.LoginCommand).ExecuteAsync(null);

            Assert.NotNull(gotApi);
            Assert.Equal("user@example.com", gotEmail);
            Assert.Equal("user@example.com", passedHint); // the username flows through as the login_hint

            var cfg = ServerProfileStore.Load();
            Assert.Equal("Local", cfg.LastServer);
            Assert.Equal("user@example.com", cfg.LastUsername);
            Assert.Equal("de", cfg.LastLanguage);

            // A fresh VM restores the remembered choices.
            var reopened = new LogonViewModel();
            Assert.Equal("user@example.com", reopened.Username);
            Assert.Equal("de", reopened.SelectedLanguage!.Code);
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

    // The self-update check (issue #271) surfaces a newer client with a download link above Login, says nothing
    // when up to date, and shows the "don't install" wording for an inconclusive (dev-SHA) comparison.
    [Fact]
    public async Task Update_check_surfaces_a_newer_client()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"logon-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        DesktopClientOptions.ApiBaseUrl = "http://localhost:8080";
        try
        {
            var vm = new LogonViewModel();

            // A strictly-newer build → a notice naming the version + a working download link.
            vm.UpdateCheck = (_, _) => Task.FromResult<UpdateInfo?>(
                new UpdateInfo("2.0.0", "https://demo/download/clients/macos/SimplArchive-2.0.0-x64.dmg", ClientUpdateKind.UpdateAvailable));
            await vm.CheckForUpdatesAsync();
            Assert.Equal(string.Format(Strings.Get("LogonUpdateAvailable"), "2.0.0"), vm.UpdateStatus);
            Assert.True(vm.HasUpdateLink);
            Assert.True(vm.OpenDownloadCommand.CanExecute(null));

            // Up to date → no notice, no link.
            vm.UpdateCheck = (_, _) => Task.FromResult<UpdateInfo?>(new UpdateInfo("1.0.0", "x", ClientUpdateKind.UpToDate));
            await vm.CheckForUpdatesAsync();
            Assert.Equal("", vm.UpdateStatus);
            Assert.False(vm.HasUpdateLink);
            Assert.False(vm.OpenDownloadCommand.CanExecute(null));

            // Inconclusive (a git short-SHA on one side) → the "don't install until advised" wording, still linked.
            vm.UpdateCheck = (_, _) => Task.FromResult<UpdateInfo?>(new UpdateInfo("a1b2c3d", "y", ClientUpdateKind.Inconclusive));
            await vm.CheckForUpdatesAsync();
            Assert.Equal(Strings.Get("LogonUpdateInconclusive"), vm.UpdateStatus);
            Assert.True(vm.HasUpdateLink);
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

    // A stuck browser sign-in must not wedge the client (ADR "Desktop logon button re-enable"): after the
    // re-enable delay the Log in button comes back, and a retry supersedes the stuck attempt (cancelling its
    // token, which would free the OAuth loopback listener) so a fresh sign-in can succeed.
    [Fact]
    public async Task A_stalled_sign_in_re_enables_the_button_and_a_retry_supersedes_it()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"logon-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        DesktopClientOptions.ApiBaseUrl = "http://localhost:8080";
        try
        {
            var vm = new LogonViewModel { ReenableDelay = TimeSpan.FromMilliseconds(50) };
            vm.ReachabilityCheck = (_, _) => Task.FromResult(true);

            // First attempt: the browser sign-in hangs until its token is cancelled (a retry supersedes it).
            var firstCancelled = false;
            vm.Authenticate = (_, ct) => Task.Run(async () =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { firstCancelled = true; throw; }
                return (OidcLoopbackAuthenticator.AuthResult?)null;
            }, CancellationToken.None);

            var firstLogin = ((IAsyncRelayCommand)vm.LoginCommand).ExecuteAsync(null);

            // The button re-enables once the stuck attempt passes the re-enable delay.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.Busy && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.False(vm.Busy); // re-enabled without restarting the client

            // Retry: succeeds. It supersedes the stuck first attempt (cancelling its token).
            vm.Authenticate = (_, _) => Task.FromResult<OidcLoopbackAuthenticator.AuthResult?>(
                new OidcLoopbackAuthenticator.AuthResult("token2", "user@example.com"));
            SimplArchiveApiClient? gotApi = null;
            vm.LoginSucceeded += (api, _) => gotApi = api;

            await ((IAsyncRelayCommand)vm.LoginCommand).ExecuteAsync(null);
            await firstLogin; // the superseded attempt completes gracefully (OCE swallowed)

            Assert.NotNull(gotApi);
            Assert.True(firstCancelled); // the stuck attempt's token was cancelled → loopback listener freed
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
