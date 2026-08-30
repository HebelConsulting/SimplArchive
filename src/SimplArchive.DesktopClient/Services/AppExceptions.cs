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
// are needed. Reconnect reloads the view + session; Sign out drops the session and returns to the logon
// window. Only one dialog shows at a time.
public static class AppExceptions
{
    private static Window? _owner;
    private static Func<bool>? _isTenantAdmin;
    private static Func<Task>? _reconnect;
    private static Func<Task>? _signIn;
    private static Action? _returnToLogon;
    private static bool _showing;

    public static void Initialize(
        Window owner,
        Func<bool> isTenantAdmin,
        Func<Task> reconnect,
        Func<Task>? signIn = null,
        Action? returnToLogon = null)
    {
        _owner = owner;
        _isTenantAdmin = isTenantAdmin;
        _reconnect = reconnect;
        _signIn = signIn;
        _returnToLogon = returnToLogon;
    }

    /// <summary>
    /// Dismissing any of these modals ends the SESSION, not the process: it drops to the logon window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three flows used to call <c>Shutdown()</c>, so a momentary network drop cost the user the whole
    /// application and every unsaved thing in it — and the only way back in was to launch it again. Returning
    /// to the logon window is the recovery the user actually wants, and it is also the honest one: the server
    /// picker is there, so they can reconnect to a DIFFERENT server without restarting.
    /// </para>
    /// <para>
    /// It routes through the ordinary logout path rather than re-implementing it, so the session teardown stays
    /// in one place: state cleared, heartbeat stopped, next sign-in forced to re-authenticate. A second copy
    /// here would be the drift CLAUDE.md's one-implementation principle names, and it would drift in the worst
    /// possible direction — a field the copy forgot to clear is the previous user's data on a shared machine.
    /// </para>
    /// <para>
    /// Falls back to <c>Shutdown()</c> when nothing is wired (the headless screenshot harness, a startup
    /// failure before the main window exists). Quitting is still reachable — from the logon window.
    /// </para>
    /// </remarks>
    internal static void ReturnToLogon()
    {
        if (_returnToLogon is { } toLogon)
        {
            toLogon();
            return;
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
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

    /// <summary>
    /// A server's session ended and could not be renewed — the user has to sign in again there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from a connectivity loss, and worth its own message: the server is reachable and answering, it
    /// simply will not accept this session any more (a revoked or expired refresh token, a deactivated account,
    /// a server restarted with new keys). Telling the user the connection was lost would send them to check
    /// their network for something their network cannot fix.
    /// </para>
    /// <para>
    /// It NAMES the server, because somebody moving between production, integration and a local stack needs to
    /// know which one dropped them; a dialog that only says "your session expired" makes them guess.
    /// </para>
    /// </remarks>
    public static void ReportSessionEnded(string apiRootUrl) => Run(() => _ = ShowSessionEndedAsync(apiRootUrl));

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
    // modal is shown again (manual retry). Sign out returns to the logon window.
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
                    ReturnToLogon();
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

    /// <summary>
    /// The session-ended modal: retries a few times, then offers to sign in again rather than only to quit.
    /// </summary>
    /// <remarks>
    /// Retrying first is deliberate. A server that is restarting refuses renewals for a few seconds and then
    /// accepts them, and throwing the user back to a login screen for that is a worse answer than waiting —
    /// the escalation exists for the sessions that are genuinely over, not for the ones that are briefly
    /// inconvenienced.
    /// </remarks>
    private const int RenewalAttemptsBeforeSignIn = 3;

    private static async Task ShowSessionEndedAsync(string apiRootUrl)
    {
        if (_owner is null || _showing)
        {
            return;
        }

        _showing = true;
        try
        {
            var attempts = 0;
            while (true)
            {
                var offerSignIn = attempts >= RenewalAttemptsBeforeSignIn;
                var message = offerSignIn
                    ? $"Your session on {apiRootUrl} has ended and could not be renewed. Sign in again to continue."
                    : $"Your session on {apiRootUrl} has ended. Trying to renew it…";

                var result = await new ConnectionLostDialog(_isTenantAdmin?.Invoke() ?? false, message)
                    .ShowDialog<string?>(_owner);

                if (result != "reconnect")
                {
                    ReturnToLogon();
                    return;
                }

                if (offerSignIn && _signIn is not null)
                {
                    await _signIn();
                    return;
                }

                attempts++;
                if (await TryReconnectAsync())
                {
                    return;
                }
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
            else if (result == "sign-out")
            {
                // A crash returns to the logon window too. The reasoning is not that a fresh session repairs
                // whatever broke — it is that the alternative was losing the application outright, and the
                // state this drops is exactly the state the unhandled exception already made untrustworthy.
                ReturnToLogon();
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
