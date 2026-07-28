using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// A UI language the client can run in (ADR "Desktop logon window"). The chosen code is remembered and applied
// as the UI culture at login (ADR "Desktop UI localization"), so the client runs in that language.
public sealed record LanguageOption(string Name, string Code);

// Backs the startup logon window (ADR "Desktop logon window", login redesign slice B): a username (email), a
// tenant (deployment) dropdown, a language dropdown, and Login. Login points the client at the chosen tenant's
// API-root URL, checks the server is reachable within ~10 s (else "No connection to the server. Retry later."),
// then runs the browser OAuth flow and raises LoginSucceeded with an authenticated api client.
public sealed partial class LogonViewModel : ObservableObject
{
    public ObservableCollection<TenantProfile> Tenants { get; } = [];
    public IReadOnlyList<LanguageOption> Languages { get; } =
        [new("English", "en"), new("Deutsch", "de"), new("Italiano", "it"), new("Español", "es")];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private TenantProfile? _selectedTenant;

    [ObservableProperty] private LanguageOption? _selectedLanguage;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _busy;

    // Supersedes an in-flight sign-in when the user clicks Log in again (ADR "Desktop logon button re-enable"):
    // cancelling it frees the OAuth loopback listener/port so a retry can start cleanly.
    private CancellationTokenSource? _loginCts;

    // How long a browser sign-in may stall before the Log in button is re-enabled for a retry. Overridable in
    // tests so the re-enable path doesn't take a real 10 s.
    internal TimeSpan ReenableDelay { get; set; } = TimeSpan.FromSeconds(10);

    // Injectable seams so a test can drive Login without a real server / browser.
    public Func<string, CancellationToken, Task<bool>> ReachabilityCheck { get; set; } = ServerReachability.CheckAsync;
    // The first parameter is the login_hint (the entered username/email) so the browser login pre-fills it; the
    // token lets a retry cancel a stuck browser flow.
    public Func<string?, CancellationToken, Task<OidcLoopbackAuthenticator.AuthResult?>> Authenticate { get; set; } =
        (hint, ct) => new OidcLoopbackAuthenticator().AuthenticateAsync(forceLogin: true, loginHint: hint, cancellationToken: ct);

    // Raised on a successful login with an authenticated api client + the resolved email; the app then opens the
    // main window.
    public event Action<SimplArchiveApiClient, string>? LoginSucceeded;

    public LogonViewModel()
    {
        var cfg = TenantProfileStore.Load();
        if (cfg.Tenants.Count == 0)
        {
            // Auto-seed a default deployment so the app is usable out of the box (ADR "Desktop logon window").
            cfg.Tenants.Add(new TenantProfile { Name = "Local", ApiRootUrl = DesktopClientOptions.ApiBaseUrl });
            TenantProfileStore.Save(cfg);
        }

        foreach (var t in cfg.Tenants)
        {
            Tenants.Add(t);
        }

        SelectedTenant = Tenants.FirstOrDefault(t => string.Equals(t.Name, cfg.LastTenant, StringComparison.OrdinalIgnoreCase)) ?? Tenants.FirstOrDefault();
        Username = cfg.LastUsername ?? "";
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == cfg.LastLanguage) ?? Languages[0];
    }

    // Reloads the tenant dropdown from the config after the manager was opened (Ctrl/Cmd+P), keeping the selected
    // one where possible — without replacing the VM (which would drop the LoginSucceeded subscription).
    public void RefreshTenants()
    {
        var previous = SelectedTenant?.Name;
        var cfg = TenantProfileStore.Load();
        Tenants.Clear();
        foreach (var t in cfg.Tenants)
        {
            Tenants.Add(t);
        }

        SelectedTenant = Tenants.FirstOrDefault(t => string.Equals(t.Name, previous, StringComparison.OrdinalIgnoreCase)) ?? Tenants.FirstOrDefault();
    }

    private bool CanLogin => SelectedTenant is not null && !Busy;

    // AllowConcurrentExecutions so the re-enabled button can start a fresh attempt while a stuck one is still
    // (logically) in flight — otherwise the command disables itself for the whole run and Busy=false wouldn't
    // re-enable it (ADR "Desktop logon button re-enable").
    [RelayCommand(CanExecute = nameof(CanLogin), AllowConcurrentExecutions = true)]
    private async Task Login()
    {
        if (SelectedTenant is null)
        {
            return;
        }

        // Supersede any in-flight sign-in — cancelling it frees the OAuth loopback listener so this attempt's
        // browser flow can bind the port (ADR "Desktop logon button re-enable").
        _loginCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loginCts = cts;

        Busy = true;
        Status = Strings.Get("StConnecting");
        DesktopClientOptions.ApiBaseUrl = SelectedTenant.ApiRootUrl.TrimEnd('/');
        Persist();

        try
        {
            bool reachable;
            try
            {
                using var reachCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                reachCts.CancelAfter(TimeSpan.FromSeconds(10));
                reachable = await ReachabilityCheck(DesktopClientOptions.ApiBaseUrl, reachCts.Token);
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
                reachable = false;
            }
            catch (HttpRequestException)
            {
                reachable = false;
            }

            if (cts.IsCancellationRequested)
            {
                return; // superseded by a retry
            }

            if (!reachable)
            {
                Status = Strings.Get("StNoConnection");
                return;
            }

            Status = Strings.Get("StOpeningBrowser");
            var authTask = Authenticate(string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(), cts.Token);

            // Re-enable the Log in button after ReenableDelay so a stuck browser sign-in (an error page, a closed
            // tab) can be retried without restarting the client. The sign-in keeps running until it completes or
            // a retry supersedes it (which cancels this cts, unblocking the loopback listener).
            var elapsed = await Task.WhenAny(authTask, Task.Delay(ReenableDelay, cts.Token));
            if (elapsed != authTask && !cts.IsCancellationRequested)
            {
                Busy = false;
                Status = Strings.Get("StStillWaiting");
            }

            var result = await authTask;
            if (cts.IsCancellationRequested)
            {
                return; // superseded by a retry
            }

            if (result is null)
            {
                Status = Strings.Get("StSignInFailed");
                return;
            }

            LoginSucceeded?.Invoke(new SimplArchiveApiClient(result.AccessToken), result.Email ?? Username);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a retry — the new attempt owns the UI now.
        }
        catch (Exception e)
        {
            if (!cts.IsCancellationRequested)
            {
                Status = string.Format(Strings.Get("StErrSignIn"), e.Message);
            }
        }
        finally
        {
            cts.Cancel(); // stop this attempt's re-enable delay
            if (_loginCts == cts)
            {
                Busy = false;
                _loginCts = null;
            }
        }
    }

    private void Persist()
    {
        var cfg = TenantProfileStore.Load();
        cfg.LastTenant = SelectedTenant?.Name;
        cfg.LastUsername = Username;
        cfg.LastLanguage = SelectedLanguage?.Code;
        TenantProfileStore.Save(cfg);
    }

    // A quick reachability probe: the server's OIDC discovery document, bounded by the caller's timeout token.
}
