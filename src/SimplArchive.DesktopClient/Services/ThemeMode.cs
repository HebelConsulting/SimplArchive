using Avalonia;
using Avalonia.Styling;

namespace SimplArchive.DesktopClient.Services;

/// <summary>Light, Dark, or whatever the operating system is currently set to.</summary>
public enum ThemeMode
{
    /// <summary>Follow the OS. The default, and what the client did exclusively before ADR 0578.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// The light/dark choice: applying it, and remembering it (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three states, not two.</b> The web client's toggle is binary, which means that once touched it can never
/// go back to following the OS — a one-way door the user did not know they were walking through. A desktop
/// application is expected to offer the third, and this one has followed the OS exclusively until now, so
/// dropping that ability while adding a switch would have been a net loss.
/// </para>
/// <para>
/// Stored per machine beside the pane layout, not on the server profile: which of your screens you are sitting
/// at is a property of you and the machine, where the STYLE (ADR 0578's accent) is a property of the server you
/// are connecting to. They look alike and belong in different places.
/// </para>
/// </remarks>
public static class ThemeModeService
{
    /// <summary>Applies a mode to the running application. <see cref="ThemeMode.System"/> hands control back.</summary>
    public static void Apply(ThemeMode mode)
    {
        if (Application.Current is not { } application)
        {
            return; // headless verification hooks that never build an application
        }

        // ThemeVariant.Default is Avalonia's "ask the platform", so System is not a third code path — it is the
        // absence of an override, which is also why switching back to it takes effect immediately.
        application.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>Reads the remembered mode, defaulting to following the OS.</summary>
    public static ThemeMode Load() =>
        Enum.TryParse<ThemeMode>(LayoutSettingsStore.Load().ThemeMode, ignoreCase: true, out var mode)
            ? mode
            : ThemeMode.System;

    /// <summary>Applies a mode and remembers it.</summary>
    public static void Save(ThemeMode mode)
    {
        Apply(mode);

        var settings = LayoutSettingsStore.Load();
        settings.ThemeMode = mode.ToString();
        LayoutSettingsStore.Save(settings);
    }
}
