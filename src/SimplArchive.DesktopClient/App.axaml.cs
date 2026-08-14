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
            var window = new MainWindow { DataContext = viewModel };

            // Wire the crash guard (ADR "Desktop crash guard") to the new main window.
            Services.AppExceptions.Initialize(window, () => viewModel.IsTenantAdmin, viewModel.ReconnectAsync);

            // Background heartbeat (ADR "Desktop session reconnect"): while logged in, probe the server so an
            // idle disconnect surfaces the reconnect modal before the user's next action fails. Stopped on logout.
            var heartbeat = new Services.SessionHeartbeat(Services.AppExceptions.ReportConnectionLost);
            heartbeat.Start();

            // Logout returns to a fresh logon window and closes the main window.
            viewModel.LogoutRequested += () =>
            {
                heartbeat.Stop();
                ShowLogon(desktop);
                window.Close();
            };

            desktop.MainWindow = window; // the main window is the app's main window before we drop the logon
            window.Show();
            logon.Close();
            _ = viewModel.InitializeSessionAsync(api, email);
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
