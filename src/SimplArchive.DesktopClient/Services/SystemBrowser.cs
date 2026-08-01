using System;
using System.Diagnostics;

namespace SimplArchive.DesktopClient.Services;

// Opens a URL in the user's default system browser, cross-platform. Shared by the OAuth loopback flow and the
// logon window's "download the newer client" link (issue #271). Best-effort — a launch failure is swallowed.
public static class SystemBrowser
{
    public static void Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }
        }
        catch (Exception)
        {
            // Launching the browser is best-effort; ignore failures.
        }
    }
}
