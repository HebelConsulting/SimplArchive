using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// What the single WebDAV ribbon button would do if clicked right now, and the tooltip that says so (#461).
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than four more members on <see cref="MainWindowViewModel"/>, which is 6,758 lines and the
/// largest entry on the 1000-line debt list (#466). CLAUDE.md treats adding to an over-limit class as needing
/// the same justification as creating one, and this genuinely is a separate thing: it answers one question —
/// which of three states the button is in — and nothing else in the window depends on it.
/// </para>
/// <para>
/// One button with three behaviours is only honest if the user can tell which they will get, so the TOOLTIP is
/// the feature here, not decoration. An icon that silently changes meaning is worse than three buttons.
/// </para>
/// </remarks>
public sealed partial class WebDavRibbonState : ObservableObject
{
    /// <summary>What clicking will do next: set up credentials, mount, or open what is already mounted.</summary>
    [ObservableProperty]
    private string _tooltip = Strings.Get("MwWebDavSetUpTip");

    /// <summary>
    /// Re-reads the state. Cheap in the common case: mounted-ness is answered LOCALLY and first, so a user who
    /// already has the volume costs no request at all; the server is asked only about credentials, which is the
    /// one thing the client cannot know for itself.
    /// </summary>
    public async Task RefreshAsync(Func<Task<bool>>? credentialsExist)
    {
        if (OsFileManager.MountedPath() is not null)
        {
            Tooltip = Strings.Get("MwWebDavOpenTip");
            return;
        }

        try
        {
            Tooltip = credentialsExist is not null && await credentialsExist()
                ? Strings.Get("MwWebDavMountTip")
                : Strings.Get("MwWebDavSetUpTip");
        }
        catch (Exception)
        {
            // Best-effort: an unreachable server leaves the button offering setup, which is harmless — the
            // dialog it opens will report the connection problem itself.
            Tooltip = Strings.Get("MwWebDavSetUpTip");
        }
    }
}
