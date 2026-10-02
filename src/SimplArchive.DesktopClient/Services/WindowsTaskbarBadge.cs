#if WINDOWS
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The Windows taskbar overlay icon, carrying the open-task count (#502).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows takes an ICON, not a number</b> — <c>ITaskbarList3::SetOverlayIcon</c> wants an <c>HICON</c> —
/// so unlike the Dock's <c>badgeLabel</c> the count has to be DRAWN. That is the whole reason this is a
/// per-platform implementation rather than one call behind an interface: the three platforms differ in what
/// they accept, not merely in how they are invoked.
/// </para>
/// <para>
/// <b>Compiled only into the Windows target.</b> The desktop client multi-targets for PKCS#11's ABI
/// (ADR 0831), and <c>System.Drawing</c> is Windows-only from .NET 6 — so this file is inside the
/// <c>WINDOWS</c> guard and the other target never sees it.
/// </para>
/// <para>
/// <b>I have never watched this run.</b> It is written from the documented API and compiles for the Windows
/// target, which is not the same as working — a wrong HWND or a failed icon handle shows up as no badge, with
/// nothing on screen to say so. That is why every branch below says what it did at Trace: on the platform I
/// cannot test, the log is the only way anybody finds out which step gave up. Verify it on a Windows machine
/// before believing it (#1403 is open for the same reason about smartcards).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskbarBadge : ITaskbarBadge
{
    private const int MaxDisplayed = 99;

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // The first three are ITaskbarList's, which ITaskbarList3 inherits: the vtable order matters, so they
        // are declared even though only HrInit is called.
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwnd, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwnd);
        void SetTabOrder(IntPtr hwnd, IntPtr insertBefore);
        void SetTabActive(IntPtr hwnd, IntPtr mdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint buttons, IntPtr button);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint buttons, IntPtr button);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    [ComImport]
    [Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance
    {
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private ITaskbarList3? _taskbar;

    public void Show(int count)
    {
        try
        {
            if (MainWindowHandle() is not { } hwnd || hwnd == IntPtr.Zero)
            {
                DesktopLog.Trace("Taskbar badge: no window handle yet; {Count} not shown.", count);
                return;
            }

            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList3)new TaskbarInstance();
                _taskbar.HrInit();
            }

            if (count <= 0)
            {
                // A NULL icon is how the overlay is removed. Passing a drawn "0" would leave a permanent zero
                // on the taskbar, which reads as "something is waiting" — the opposite of the point.
                _taskbar.SetOverlayIcon(hwnd, IntPtr.Zero, null);
                DesktopLog.Trace("Taskbar badge: cleared.");
                return;
            }

            var icon = DrawCount(count);
            if (icon == IntPtr.Zero)
            {
                DesktopLog.Trace("Taskbar badge: could not draw an icon for {Count}; not shown.", count);
                return;
            }

            try
            {
                _taskbar.SetOverlayIcon(hwnd, icon, $"{count} open tasks");
                DesktopLog.Trace("Taskbar badge: set ({Count}).", count);
            }
            finally
            {
                // The shell copies the icon, so the handle is ours to release — and an overlay set every time
                // the count changes would otherwise leak one GDI handle per change, which ends in a process
                // that cannot draw anything at all.
                DestroyIcon(icon);
            }
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "Taskbar badge: could not set the overlay to {Count}; the count is still in the window.", count);
        }
    }

    /// <summary>The main window's HWND, or null before a window exists.</summary>
    /// <remarks>
    /// Read at CALL time rather than taken in the constructor: the badge is built with the view-model, and the
    /// window is built from it, so requiring the handle up front would invert who builds whom (ADR 0730).
    /// </remarks>
    private static IntPtr? MainWindowHandle() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.TryGetPlatformHandle()?.Handle
            : null;

    /// <summary>Draws the count into a 16×16 overlay and returns an HICON, or zero.</summary>
    /// <remarks>
    /// Capped at <see cref="MaxDisplayed"/>+: three digits do not fit legibly in sixteen pixels, and an
    /// unreadable smudge is worse than "lots" — the badge's job is "there is work", not an exact figure, which
    /// the window carries.
    /// </remarks>
    private static IntPtr DrawCount(int count)
    {
        var text = count > MaxDisplayed ? "99+" : count.ToString();

        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(Brushes.Firebrick, 0, 0, 15, 15);

            using var font = new Font(FontFamily.GenericSansSerif, text.Length > 2 ? 5f : 7f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            graphics.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 16, 16), format);
        }

        return bitmap.GetHicon();
    }
}
#endif
