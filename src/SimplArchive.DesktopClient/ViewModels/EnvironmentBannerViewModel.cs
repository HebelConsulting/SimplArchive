using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The thin environment strip at the top of the main window (#501): the server profile's declared
/// environment, on its colour, or nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a banner and not the accent:</b> ADR 0578's per-profile styles can already paint production
/// differently, but they spend the accent — which has three jobs (primary action, selection, focus) — on a
/// signal the user needs to register once per window. And red, the one colour that MEANS production, is
/// unusable as an accent because it puts a red Save beside a red Delete. A strip that carries no actions is
/// information, not decoration — which is what distinguishes it from the brand-coloured chrome band ADR 0578
/// removed as dated: that band said nothing, this one says which system you are about to change.
/// </para>
/// <para>
/// Its own class rather than properties on <see cref="MainWindowViewModel"/>: that file is on the 1000-line
/// standing-debt list (#466) and may only get smaller, and the banner is one thing with three facets.
/// </para>
/// </remarks>
public sealed partial class EnvironmentBannerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    private string _name = string.Empty;

    [ObservableProperty] private IBrush _background = Brushes.Transparent;

    /// <summary>Empty and unknown both mean hidden — the single-deployment norm (#501).</summary>
    public bool IsShown => Name.Length > 0;

    /// <summary>Applies a stored environment id from the chosen server profile; empty/unknown clears.</summary>
    public void Set(string? environmentId)
    {
        var level = EnvironmentLevels.Resolve(environmentId);
        Background = level is null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(level.Color));
        Name = level?.Name ?? string.Empty;
    }
}
