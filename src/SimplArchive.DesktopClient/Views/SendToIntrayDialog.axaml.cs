using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The "Send to…" dialog (ADR 0532): pick a single destination — a group the caller belongs to, or another tenant
// user — for an own intray item. ShowDialog<IntrayTargetInfo?> returns the chosen target, or null if cancelled /
// nothing to choose. The controller enforces "exactly one target", so a single-select is all this offers.
public partial class SendToIntrayDialog : Window
{
    // Kept in ItemsSource order so the selected index maps back to a target (the combobox shows label strings —
    // a private display type can't back a compiled XAML binding).
    private readonly IReadOnlyList<IntrayApi.IntrayTargetInfo> _targets;

    public SendToIntrayDialog() : this("", [])
    {
    }

    public SendToIntrayDialog(string itemName, IReadOnlyList<IntrayApi.IntrayTargetInfo> targets)
    {
        InitializeComponent();
        _targets = targets;
        PromptText.Text = string.Format(Strings.Get("IntraySendPrompt"), itemName);

        var groupTag = Strings.Get("IntrayGroupTag");
        var userTag = Strings.Get("IntrayUserTag");
        TargetBox.ItemsSource = targets.Select(t => $"{(t.IsGroup ? groupTag : userTag)} · {t.Name}").ToList();
        if (targets.Count > 0)
        {
            TargetBox.SelectedIndex = 0;
        }
        else
        {
            // Nothing to send to — show the hint and disable the confirm.
            EmptyText.IsVisible = true;
            TargetBox.IsVisible = false;
            SendButton.IsEnabled = false;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSend(object? sender, RoutedEventArgs e) =>
        Close(TargetBox.SelectedIndex >= 0 && TargetBox.SelectedIndex < _targets.Count ? _targets[TargetBox.SelectedIndex] : null);
}
