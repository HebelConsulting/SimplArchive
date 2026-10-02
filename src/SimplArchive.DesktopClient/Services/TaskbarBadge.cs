using System;
using System.Runtime.InteropServices;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Shows the open-task count outside the window — on the Dock, the taskbar, or the launcher (#502).
/// </summary>
/// <remarks>
/// <para>
/// <b>The point of a task inbox is that it tells you work is waiting</b>, and the in-app badge can only do
/// that while the window is in front of you. Every mail and chat client puts the count on the Dock icon for
/// exactly this reason.
/// </para>
/// <para>
/// <b>Three platforms, three genuinely different mechanisms</b> — none of them in Avalonia's cross-platform
/// surface, which is why this is an interface rather than a call:
/// </para>
/// <list type="bullet">
/// <item><b>macOS</b> — <c>NSDockTile.badgeLabel</c>, a short string drawn on the Dock icon. Implemented.</item>
/// <item><b>Windows</b> — the taskbar overlay icon (<c>ITaskbarList3::SetOverlayIcon</c>), which takes an
/// ICON rather than a number, so a count must be rendered into a small bitmap. Not implemented.</item>
/// <item><b>Linux</b> — the Unity <c>LauncherEntry</c> D-Bus interface, which does take a number and is
/// honoured by several docks despite the name, but is desktop-environment dependent. Not implemented.</item>
/// </list>
/// </remarks>
public interface ITaskbarBadge
{
    /// <summary>Shows <paramref name="count"/>, or clears the badge when it is zero.</summary>
    void Show(int count);
}

/// <summary>Picks the badge for the platform this build is running on.</summary>
public static class TaskbarBadge
{
    /// <summary>The badge for the current platform; never null, and never throws for lack of one.</summary>
    public static ITaskbarBadge ForThisPlatform()
    {
        ITaskbarBadge badge =
            OperatingSystem.IsMacOS() ? new MacDockBadge()
#if WINDOWS
            : OperatingSystem.IsWindows() ? new WindowsTaskbarBadge()
#endif
            : new NoTaskbarBadge();

        DesktopLog.Trace("Taskbar badge: using {Badge} on {Platform}.",
            badge.GetType().Name, System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        return badge;
    }
}

/// <summary>
/// The platforms whose mechanism exists but is not built yet — a deliberate no-op, named (#502).
/// </summary>
/// <remarks>
/// <b>It does nothing, on purpose, and that is a stated gap rather than an assumed one.</b> Writing the
/// Windows overlay-icon and Linux D-Bus paths without a machine to watch them run would be two thirds of this
/// feature existing only on paper — and in this layer a failure is a silent absent badge rather than an error,
/// so nobody would ever learn it did not work. The mechanisms are recorded on <see cref="ITaskbarBadge"/>.
///
/// <para><b>The trigger that retires this:</b> a Windows or Linux machine to verify on. Until then the count
/// is visible in the window on those platforms exactly as it was before — nothing regressed, one thing is
/// simply not gained.</para>
/// </remarks>
public sealed class NoTaskbarBadge : ITaskbarBadge
{
    public void Show(int count) =>
        // Says so rather than returning silently: on a platform with no implementation, an absent badge and a
        // badge that failed are indistinguishable from outside, and this is the line that tells them apart.
        DesktopLog.Trace("Taskbar badge: no implementation on this platform; {Count} not shown.", count);
}

/// <summary>The macOS Dock badge, via <c>NSDockTile.badgeLabel</c> (#502).</summary>
/// <remarks>
/// <para>
/// Objective-C runtime interop, because Avalonia exposes no Dock tile. The chain is
/// <c>[[NSApplication sharedApplication] dockTile]</c> then <c>setBadgeLabel:</c>, and the label is an
/// <c>NSString</c> built from UTF-8 bytes.
/// </para>
/// <para>
/// <b>Every failure here is swallowed, which is right exactly once.</b> A Dock badge is decoration on top of
/// a count the window already shows: if the interop finds no <c>NSApplication</c> — a headless run, a process
/// with no Dock presence, a future macOS that moves this — the correct outcome is no badge, never a crash in
/// the middle of refreshing somebody's task list. The count itself is unaffected.
/// </para>
/// </remarks>
public sealed class MacDockBadge : ITaskbarBadge
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    [DllImport(Objc, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendWith(IntPtr receiver, IntPtr selector, IntPtr argument);

    public void Show(int count)
    {
        try
        {
            var application = Send(GetClass("NSApplication"), Selector("sharedApplication"));
            if (application == IntPtr.Zero)
            {
                DesktopLog.Trace("Dock badge: no NSApplication — headless or no Dock presence; {Count} not shown.", count);
                return;
            }

            var tile = Send(application, Selector("dockTile"));
            if (tile == IntPtr.Zero)
            {
                DesktopLog.Trace("Dock badge: NSApplication has no dockTile; {Count} not shown.", count);
                return;
            }

            // An EMPTY label clears the badge; macOS draws nothing for "". Passing "0" would leave a permanent
            // zero sitting on the Dock, which reads as "something is waiting" to anyone glancing at it — the
            // opposite of what this feature is for.
            SendWith(tile, Selector("setBadgeLabel:"), NsString(count > 0 ? count.ToString() : string.Empty));
            DesktopLog.Trace("Dock badge: {Action} ({Count}).", count > 0 ? "set" : "cleared", count);
        }
        catch (Exception e)
        {
            // See the type's remarks: a missing badge is the correct failure, a crash is not. But it says what
            // happened, or the one platform where this DOES work would fail as silently as the two where it is
            // not implemented.
            DesktopLog.Warn(e, "Dock badge: could not set the badge to {Count}; the count is still in the window.", count);
        }
    }

    private static IntPtr NsString(string value)
    {
        var utf8 = Marshal.StringToHGlobalAnsi(value);
        try
        {
            return SendWith(GetClass("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeHGlobal(utf8);
        }
    }
}
