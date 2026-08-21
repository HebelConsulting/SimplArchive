using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// One create the server offered for the right-clicked folder — an entry in the tree menu's "New" submenu (#673).
/// </summary>
/// <remarks>
/// <para>
/// The creates had to stop being static markup once they became server-supplied: a folder says what it admits,
/// and how many entries that is depends on the folder. Avalonia's menu takes either literal items or a bound
/// collection and never both, so the dynamic entries went into a <b>submenu</b> of their own rather than the
/// fifteen static ones being rebuilt here to join them. That costs a hover to reach a create (ADR 0550 would
/// rather it did not), and it bought not rewriting a menu that nothing could verify without opening it by hand.
/// </para>
/// <para>
/// <b>The command is a delegate the VIEW supplies.</b> The handlers stay in <c>MainWindow.axaml.cs</c> next to
/// the dialogs they open and the window they need; this type only carries what to show and what to run.
/// </para>
/// <para>
/// <b>Every property here is bound, and that was not always true.</b> <c>Icon</c> was set and never read for as
/// long as this type existed — the submenu's <c>ItemContainerTheme</c> bound <c>Header</c> and <c>Command</c>
/// and nothing else, so these were the only entries in that menu with no glyph. Nothing failed; the icons were
/// simply absent, and only a rendered screenshot showed it (`--menu`). If you add a property, bind it in that
/// theme in the same change, or it is decoration.
/// </para>
/// </remarks>
public sealed class TreeMenuEntry
{
    private TreeMenuEntry()
    {
    }

    /// <summary>The label — the mask's name as this tenant calls it today.</summary>
    /// <remarks>
    /// Not localised, and deliberately: it comes from the mask's current version, so it is whatever this tenant
    /// renamed it to. A translation table here could only ever cover the masks the application ships, which is
    /// the limitation the whole change exists to remove — and it would show the shipped name for a mask
    /// somebody renamed.
    /// </remarks>
    public string Header { get; private init; } = string.Empty;

    /// <summary>The Material Design Icons glyph the created thing will itself wear.</summary>
    public string Icon { get; private init; } = string.Empty;

    public ICommand? Command { get; private init; }

    public static TreeMenuEntry Create(string maskName, string icon, Action run) =>
        new() { Header = maskName, Icon = icon, Command = new RelayCommand(run) };
}
