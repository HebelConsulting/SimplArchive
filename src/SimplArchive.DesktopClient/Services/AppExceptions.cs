using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SimplArchive.DesktopClient.Views;

namespace SimplArchive.DesktopClient.Services;

// A last-resort crash guard (ADR "Desktop crash guard"): instead of letting an unhandled exception take the
// app down, it surfaces the "lost connection" modal. Wired to the .NET global exception events and used by
// the Safe.Fire wrapper around the app's async void handlers — Avalonia has no single UI-thread hook, so both
// are needed. Reconnect reloads the view + session; Close exits. Only one dialog shows at a time.
public static class AppExceptions
{
    private static Window? _owner;
    private static Func<bool>? _isTenantAdmin;
    private static Func<Task>? _reconnect;
    private static bool _showing;

    public static void Initialize(Window owner, Func<bool> isTenantAdmin, Func<Task> reconnect)
    {
        _owner = owner;
        _isTenantAdmin = isTenantAdmin;
        _reconnect = reconnect;
    }

    // Safe to call from any thread — marshals to the UI thread. A connectivity failure (server unreachable)
    // shows the reconnect modal that reappears until reachable (ADR "Desktop session reconnect"); any other
    // unhandled exception shows the one-shot crash dialog.
    public static void Report(Exception exception)
    {
        if (IsConnectivityError(exception))
        {
            ReportConnectionLost();
            return;
        }

        Run(() => _ = ShowCrashAsync(exception));
    }

    // Raised by the background heartbeat (ADR "Desktop session reconnect") when an idle probe finds the server
    // unreachable — surfaces the same reconnect modal without an exception.
    public static void ReportConnectionLost() => Run(() => _ = ShowReconnectAsync());

    private static void Run(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    // A connectivity failure — the server is unreachable — as opposed to a genuine bug. Only these drive the
    // reconnect modal; a real crash (e.g. a NullReferenceException) shouldn't loop a "reconnect" prompt.
    internal static bool IsConnectivityError(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    // The reconnect modal: reappears until the server is reachable again (ADR "Desktop session reconnect").
    // Reconnect runs a bounded reachability probe + a session reload; on success the loop ends, on failure the
    // modal is shown again (manual retry). Close exits the app.
    private static async Task ShowReconnectAsync()
    {
        if (_owner is null || _showing)
        {
            return;
        }

        _showing = true;
        try
        {
            while (true)
            {
                var showDetails = _isTenantAdmin?.Invoke() ?? false;
                var result = await new ConnectionLostDialog(showDetails, "The connection to the server was lost or the server is unreachable.")
                    .ShowDialog<string?>(_owner);

                if (result != "reconnect")
                {
                    (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                    return;
                }

                if (await TryReconnectAsync())
                {
                    return; // reachable + session reloaded — dismiss the modal
                }
                // still unreachable → loop and show the modal again
            }
        }
        finally
        {
            _showing = false;
        }
    }

    private static async Task<bool> TryReconnectAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (!await ServerReachability.CheckAsync(DesktopClientOptions.ApiBaseUrl, cts.Token))
            {
                return false;
            }

            if (_reconnect is not null)
            {
                await _reconnect();
            }

            return true;
        }
        catch (Exception)
        {
            return false; // still unreachable / the reload failed — re-show the modal
        }
    }

    // The one-shot crash dialog for a genuine (non-connectivity) unhandled exception.
    private static async Task ShowCrashAsync(Exception exception)
    {
        // Before the main window exists (a startup failure) or while a dialog is already up, there's nothing
        // to own/stack a modal on — swallow rather than crash.
        if (_owner is null || _showing)
        {
            return;
        }

        _showing = true;
        try
        {
            var showDetails = _isTenantAdmin?.Invoke() ?? false;
            var result = await new ConnectionLostDialog(showDetails, exception.ToString()).ShowDialog<string?>(_owner);

            if (result == "reconnect" && _reconnect is not null)
            {
                try
                {
                    await _reconnect();
                }
                catch
                {
                    // Don't loop the dialog if reconnecting also fails — leave it for the next user action.
                }
            }
            else if (result == "close")
            {
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        }
        finally
        {
            _showing = false;
        }
    }
}

// Runs an async operation from a (sync) event handler, routing any exception to the crash guard instead of
// letting an async-void exception crash the app. Use: `OnClick(...) => Safe.Fire(() => vm.DoAsync());`.
public static class Safe
{
    public static void Fire(Func<Task> action) => _ = RunAsync(action);

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e)
        {
            AppExceptions.Report(e);
        }
    }
}
