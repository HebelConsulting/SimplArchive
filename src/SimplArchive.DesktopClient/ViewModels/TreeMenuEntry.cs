using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// One entry in the tree's context menu — including the separators, because the menu is now built rather than
/// written (#673).
/// </summary>
/// <remarks>
/// <para>
/// The menu had to stop being static markup once the CREATES became server-supplied: a folder says what it
/// admits, and how many entries that is depends on the folder. Avalonia's menu takes either literal items or a
/// bound collection, never both, so the entries that never change are built here too rather than the dynamic
/// ones being exiled to a submenu — which would have put an extra click and a hover between the user and every
/// create (ADR 0550).
/// </para>
/// <para>
/// <b>The command is a delegate the VIEW supplies.</b> The fifteen handlers stay in <c>MainWindow.axaml.cs</c>
/// where they already are, next to the dialogs they open and the window they need; this type only decides what
/// appears and in what order. Moving the bodies would have shifted a thousand lines between two files that are
/// both already over the size limit, which is churn no reviewer can check and neither ceiling would forgive.
/// </para>
/// </remarks>
public sealed class TreeMenuEntry
{
    private TreeMenuEntry()
    {
    }

    /// <summary>The label, already localised — a separator has none.</summary>
    public string Header { get; private init; } = string.Empty;

    /// <summary>The Material Design icon name, or empty where the entry has no icon.</summary>
    public string Icon { get; private init; } = string.Empty;

    /// <summary>Whether this is a rule rather than an item. Bound by the container theme, not by a template.</summary>
    public bool IsSeparator { get; private init; }

    public ICommand? Command { get; private init; }

    public static TreeMenuEntry Item(string header, string icon, Action run) =>
        new() { Header = header, Icon = icon, Command = new RelayCommand(run) };

    /// <summary>
    /// A create the SERVER offered — its label is the mask's name as the tenant calls it today.
    /// </summary>
    /// <remarks>
    /// Not localised, and deliberately: the name comes from the mask's current version, so it is whatever this
    /// tenant renamed it to. A translation table here could only ever cover the masks the application ships,
    /// which is the limitation the whole change exists to remove — and it would silently show the shipped name
    /// for a mask somebody renamed.
    /// </remarks>
    public static TreeMenuEntry Create(string maskName, string icon, Action run) =>
        new() { Header = maskName, Icon = icon, Command = new RelayCommand(run) };

    public static TreeMenuEntry Separator() => new() { IsSeparator = true };
}
