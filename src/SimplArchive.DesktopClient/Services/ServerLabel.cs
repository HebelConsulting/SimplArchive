using System;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// How the connected server is named in the window title (#1123).
/// </summary>
/// <remarks>
/// <para>
/// Reported from use: once the client is started there is no way to see which server it is talking to. The
/// address is chosen at the logon window and then never shown again, so two clients open against a test server
/// and a live one are indistinguishable — including in the dock and the alt-tab list, which is where the
/// mistake actually gets made.
/// </para>
/// <para>
/// The HOST, not the whole URL: the scheme is the same on every entry and the path is empty, so they spend
/// title-bar width without telling anyone anything. The PORT is kept when there is one, because on a developer
/// machine it is the only thing distinguishing two servers (<c>localhost:8080</c> from <c>localhost:5000</c>) —
/// dropping it would collapse exactly the case this is for.
/// </para>
/// <para>
/// Its own type rather than a string built in the view, so the parsing is testable without a display — and
/// because a title is one of those things nothing fails over when it is wrong.
/// </para>
/// </remarks>
public static class ServerLabel
{
    /// <summary>The product name alone, for a window with no server yet.</summary>
    public const string ProductName = "SimplArchive";

    /// <summary>
    /// The window title for a client connected to <paramref name="apiBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Falls back to the bare product name for anything unparseable rather than showing a broken string: a
    /// title is decoration, and no caller should have to handle an exception from one.
    /// </remarks>
    public static string TitleFor(string? apiBaseUrl) =>
        HostOf(apiBaseUrl) is { Length: > 0 } host ? $"{ProductName} — {host}" : ProductName;

    /// <summary>The host (with its port, when the URL carries one), or an empty string if it cannot be read.</summary>
    public static string HostOf(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl)
            || !Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        // IsDefaultPort covers both schemes, so https://host stays "host" while http://host:8080 keeps its
        // port. Authority would also carry userinfo, which must never reach a title bar.
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}
