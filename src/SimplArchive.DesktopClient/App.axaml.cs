using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.DesktopClient.Views;

namespace SimplArchive.DesktopClient;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The remembered light/dark choice (ADR 0578), before any window is built so nothing renders in one
            // theme and repaints into the other.
            //
            // Inside this branch deliberately: the headless verification hooks have no desktop lifetime, so a
            // developer whose own client is set to Dark cannot make --screenshot render dark and quietly change
            // every figure in the user manual.
            Services.ThemeModeService.Apply(Services.ThemeModeService.Load());

            ShowLogon(desktop);
        }

        SetMacDockIcon();

        base.OnFrameworkInitializationCompleted();
    }

    // The startup flow (ADR "Desktop logon window", login redesign slice B): show the logon window; on a
    // successful login open the main window (already authenticated) and close the logon; on logout return here.
    private static void ShowLogon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var logonVm = new LogonViewModel();
        // Render the logon window in the remembered language (ADR "Desktop UI localization"); the culture is
        // applied again at login so a just-changed selection takes effect for the main window.
        SimplArchive.Localization.Culture.Apply(logonVm.SelectedLanguage?.Code);
        var logon = new LogonWindow { DataContext = logonVm };
        logonVm.LoginSucceeded += (api, email) =>
        {
            SimplArchive.Localization.Culture.Apply(logonVm.SelectedLanguage?.Code);
            var viewModel = new MainWindowViewModel();
            // The environment strip (#501) comes from the profile the user just logged into — the same source
            // as its style, decided at the same moment.
            viewModel.EnvBanner.Set(logonVm.SelectedServer?.Environment);
            var window = new MainWindow { DataContext = viewModel };

            // Wire the crash guard (ADR "Desktop crash guard") to the new main window. The sign-in hook is what
            // lets a session that cannot be renewed escalate to a real login instead of only offering to quit.
            //
            // `returnToLogon` is what the modals' second button now does instead of quitting the app. It is
            // the LOGOUT command deliberately, not LoginCommand: LoginCommand re-runs the browser OAuth flow
            // against the server already configured, while what a disconnected user needs is the logon window
            // — the one place that lets them pick a different server. Logout also clears the session state and
            // raises LogoutRequested below, which stops the heartbeat and closes this window, so the whole
            // teardown stays the single path it already was.
            Services.AppExceptions.Initialize(
                window,
                () => viewModel.IsTenantAdmin,
                viewModel.ReconnectAsync,
                () => viewModel.LoginCommand.ExecuteAsync(null),
                () => viewModel.LogoutCommand.Execute(null));

            // The card PIN prompt (#1353, ADR 0832), owned by the window because a dialog needs one — ADR 0730's
            // "what the view supplies stays a callback": CardSession is a static reached from a content read,
            // and the window it must be modal over does not exist when that static is first touched.
            //
            // Dispatched to the UI thread rather than assumed to be on it: the funnel's await usually resumes
            // there, but "usually" is not a property to build a modal on, and InvokeAsync is a no-op when we
            // are already there.
            Services.CardSession.PinPrompt = async () => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                async () =>
                {
                    var pinDialog = new CardPinDialog();
                    await pinDialog.ShowDialog(window);
                    return pinDialog.Pin;
                });

            // The card leaving the reader discards what it decrypted (#1353 decision 2, ADR 0832). The user
            // stays signed in — the owner's refinement of that decision — so this closes CONTENT, not the
            // session: an unattended screen stops showing a decrypted document, and metadata browsing is
            // unaffected because none of it was ever enveloped.
            var cardWatcher = new Services.CardPresenceWatcher(() =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    viewModel.DiscardDecryptedContent(
                        SimplArchive.Localization.Strings.Get("CardRemovedContentClosed"))));
            cardWatcher.Start();

            // Signing out drops the card session with everything else: the login it holds is a live capability
            // on the user's own key, and leaving it open past their session would outlive the thing that
            // authorised it.
            viewModel.LogoutRequested += Services.CardSession.Close;

            // And the content funnel's authenticated client goes with it: a client left behind after the
            // session ends is the drift that left tabs holding a stale API client after sign-out.
            viewModel.LogoutRequested += () => Services.ApiCore.Authenticated = null;

            // A session that ends is NOT a connectivity failure — the server is answering, it just will not
            // accept this session any more — so it gets its own modal, naming the server it happened on.
            Services.RenewingAuthHandler.SessionEnded += Services.AppExceptions.ReportSessionEnded;

            // Background heartbeat (ADR "Desktop session reconnect"): while logged in, probe the server so an
            // idle disconnect surfaces the reconnect modal before the user's next action fails. Stopped on logout.
            var heartbeat = new Services.SessionHeartbeat(Services.AppExceptions.ReportConnectionLost);
            heartbeat.Start();

            // Logout returns to a fresh logon window and closes the main window.
            viewModel.LogoutRequested += () =>
            {
                cardWatcher.Stop();
                cardWatcher.Dispose();
                heartbeat.Stop();
                ShowLogon(desktop);
                window.Close();
            };

            desktop.MainWindow = window; // the main window is the app's main window before we drop the logon
            window.Show();
            logon.Close();
            // Safe.Fire, never a bare `_ =` (ADR 0275's discipline, #1077): the bootstrap reports its own
            // failures, and this wrapper is what catches anything it cannot — an async void that throws past
            // both is an app-killer, and a swallowed one is a workbench that silently says "Not logged in.".
            Services.Safe.Fire(() => viewModel.InitializeSessionAsync(api, email));
        };

        desktop.MainWindow = logon;
        logon.Show();
    }

    // The macOS Dock icon isn't driven by Window.Icon — set it from the embedded PNG via AppKit (best-effort,
    // no-op off macOS). See ADR "Desktop app icon".
    private static void SetMacDockIcon()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://SimplArchive.DesktopClient/Assets/cabinet.png"));
            var path = Path.Combine(Path.GetTempPath(), "simplarchive-dock-icon.png");
            using (var file = File.Create(path))
            {
                stream.CopyTo(file);
            }

            MacDockIcon.TrySet(path);
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
