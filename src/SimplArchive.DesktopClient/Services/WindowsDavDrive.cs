using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Maps the WebDAV volume to a Windows drive letter, and reads existing mappings back (#820).
/// </summary>
/// <remarks>
/// <para>
/// <c>WNetAddConnection3</c> with <c>CONNECT_INTERACTIVE</c>, not <c>net use</c>: a GUI process has no console
/// for <c>net</c> to prompt on, so a mount that needs credentials died silently — and passing the password on
/// the command line ourselves would put a credential into process listings and one grep away from a log. The
/// interactive flag shows the SYSTEM'S credential dialog instead; the password never passes through this
/// process at all, which is also why nothing here can leak it into the log (ADR 0613's rule).
/// </para>
/// <para>
/// <c>WNetGetConnection</c> is the read side: a mapped network drive's REMOTE path, which is what lets
/// "already mounted?" be answered by HOST rather than by volume label. The label test it replaces asked the
/// wrong question — a WebDAV mapping commonly reports no label at all, so the client concluded "not mounted"
/// forever and re-opened the bare UNC every time (the same match-by-name rot the macOS side already fixed).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsDavDrive
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectUpdateProfile = 0x1;  // persist across reboots — the half of #461 that matters
    private const int ConnectInteractive = 0x8;    // allowed to show the system credential dialog
    private const int ConnectPrompt = 0x10;        // ...and to show it even before trying cached credentials fail

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection3W(nint hwndOwner, ref NetResource netResource, string? password, string? userName, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    /// <summary>Maps <paramref name="unc"/> onto <paramref name="letter"/>:, prompting for credentials in the
    /// system dialog when needed. Returns the Win32 error code (0 = success).</summary>
    public static int Map(nint ownerWindow, string unc, char letter)
    {
        var resource = new NetResource
        {
            Type = ResourceTypeDisk,
            LocalName = $"{letter}:",
            RemoteName = unc,
        };
        return WNetAddConnection3W(ownerWindow, ref resource, null, null,
            ConnectUpdateProfile | ConnectInteractive | ConnectPrompt);
    }

    /// <summary>The remote UNC a drive letter is mapped to, or null when it is not a mapped network drive.</summary>
    public static string? RemoteOf(char letter)
    {
        var buffer = new StringBuilder(1024);
        var length = buffer.Capacity;
        return WNetGetConnectionW($"{letter}:", buffer, ref length) == 0 ? buffer.ToString() : null;
    }
}
