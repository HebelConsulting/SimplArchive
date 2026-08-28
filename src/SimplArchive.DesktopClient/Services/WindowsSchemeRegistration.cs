using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Registers the <c>simplarchive://</c> URL scheme for the current Windows user (#761).
/// </summary>
/// <remarks>
/// <para>
/// HKCU\Software\Classes, deliberately: it needs no elevation, works for the portable .zip distribution
/// (which has no installer to do this), and follows the executable when the user moves it — the value is
/// simply rewritten at the next start. macOS and Linux register declaratively instead (Info.plist
/// CFBundleURLTypes / a .desktop MimeType entry), both stamped at packaging; an unbundled `dotnet run` on
/// those platforms simply has no scheme, which is fine — the paste path (Go to link…) always works.
/// </para>
/// <para>
/// Never throws: scheme registration is a convenience, and a locked-down registry must not keep the
/// client from starting (the DesktopLog.Initialize contract, one seam over).
/// </para>
/// </remarks>
internal static class WindowsSchemeRegistration
{
    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            RegisterCurrentUser();
        }
        catch (Exception e)
        {
            DesktopLog.Debug("Deep link: scheme registration skipped ({Message})", e.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterCurrentUser()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (exe is null)
        {
            return;
        }

        using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{DeepLinks.Scheme}");
        root.SetValue(null, "URL:SimplArchive deep link");
        root.SetValue("URL Protocol", string.Empty);
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue(null, $"\"{exe}\" \"%1\"");
        DesktopLog.Debug("Deep link: {Scheme}:// registered for the current user -> {Exe}", DeepLinks.Scheme, exe);
    }
}
