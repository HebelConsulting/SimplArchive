using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Joins several staged inbox items into one (issue #487). <c>ShowDialog&lt;JoinItemsDialog.Result?&gt;</c>
/// returns the item names in the order chosen plus an optional name for the result, or null if cancelled.
/// </summary>
/// <remarks>
/// The order is the point of the dialog. A join with no stated order is a coin flip, and "which half of the
/// stack came off the scanner first" is knowledge only the person at the scanner has — so the selection arrives
/// in list order and can be rearranged before anything is sent.
/// </remarks>
public partial class JoinItemsDialog : Window
{
    /// <summary>The names to join, in order, and what to call the result (empty → the server derives one).</summary>
    public sealed record Result(IReadOnlyList<string> Names, string? Name);

    private readonly ObservableCollection<string> _names = [];

    public JoinItemsDialog() : this([])
    {
    }

    public JoinItemsDialog(IReadOnlyList<string> names)
    {
        InitializeComponent();

        foreach (var name in names)
        {
            _names.Add(name);
        }

        PromptText.Text = string.Format(Strings.Get("InboxJoinPrompt"), _names.Count);
        ItemList.ItemsSource = _names;
        if (_names.Count > 0)
        {
            ItemList.SelectedIndex = 0;
        }

        JoinButton.IsEnabled = _names.Count > 1;
    }

    /// <summary>The names in their current order. Public so a headless test can drive the dialog.</summary>
    public IReadOnlyList<string> CurrentOrder => _names.ToList();

    public void MoveUp(int index) => Move(index, -1);

    public void MoveDown(int index) => Move(index, +1);

    // The selection follows the item, so a second click moves the same one again (ListOrder.Move).
    private void Move(int index, int delta) => ItemList.SelectedIndex = ListOrder.Move(_names, index, delta);

    private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveUp(ItemList.SelectedIndex);

    private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveDown(ItemList.SelectedIndex);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnJoin(object? sender, RoutedEventArgs e) =>
        Close(new Result(CurrentOrder, string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim()));
}
