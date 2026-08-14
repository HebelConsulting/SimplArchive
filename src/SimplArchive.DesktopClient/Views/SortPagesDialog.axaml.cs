using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Puts the pages of a staged inbox item in a different order (issue #487). <c>ShowDialog&lt;IReadOnlyList&lt;int&gt;?&gt;</c>
/// returns the chosen order as 1-based ORIGINAL page numbers, or null if cancelled.
/// </summary>
/// <remarks>
/// <para>
/// The dialog is deliberately dumb: it is handed the page pictures and hands back an order. It does not know
/// what a PDF is, does not talk to the Api, and cannot half-apply anything — the single request that rewrites
/// the file happens after it closes. That is also what makes it testable without a display.
/// </para>
/// <para>
/// Reordering is by selection plus move-earlier/move-later rather than drag-and-drop. Drag is the obvious
/// gesture and it is worth adding, but it must not be the ONLY one: a selection-plus-button path is reachable
/// by keyboard and by a user who cannot comfortably drag, and it is what a headless test can drive.
/// </para>
/// </remarks>
public partial class SortPagesDialog : Window
{
    private readonly ObservableCollection<InboxPageViewModel> _pages = [];

    public SortPagesDialog() : this(string.Empty, [])
    {
    }

    public SortPagesDialog(string itemName, IReadOnlyList<Bitmap?> pageImages)
    {
        InitializeComponent();

        for (var i = 0; i < pageImages.Count; i++)
        {
            _pages.Add(new InboxPageViewModel(i + 1, pageImages[i]));
        }

        PromptText.Text = string.Format(Strings.Get("InboxSortPrompt"), itemName, _pages.Count);
        PageList.ItemsSource = _pages;
        if (_pages.Count > 0)
        {
            PageList.SelectedIndex = 0;
        }
    }

    /// <summary>The order the pages are currently in, as 1-based original page numbers.</summary>
    public IReadOnlyList<int> CurrentOrder => _pages.Select(p => p.OriginalNumber).ToList();

    /// <summary>Moves the page at <paramref name="index"/> one place earlier. Public so a test can drive it.</summary>
    public void MoveEarlier(int index) => Move(index, -1);

    /// <summary>Moves the page at <paramref name="index"/> one place later.</summary>
    public void MoveLater(int index) => Move(index, +1);

    /// <summary>Reverses the whole order — the one bulk case worth a button (a stack scanned back-to-front).</summary>
    public void Reverse()
    {
        var reversed = _pages.Reverse().ToList();
        _pages.Clear();
        foreach (var page in reversed)
        {
            _pages.Add(page);
        }

        PageList.SelectedIndex = _pages.Count > 0 ? 0 : -1;
    }

    // The selection follows the page, not the slot — see ListOrder.Move, which both reorder dialogs share.
    private void Move(int index, int delta) => PageList.SelectedIndex = ListOrder.Move(_pages, index, delta);

    private void OnMoveEarlier(object? sender, RoutedEventArgs e) => MoveEarlier(PageList.SelectedIndex);

    private void OnMoveLater(object? sender, RoutedEventArgs e) => MoveLater(PageList.SelectedIndex);

    private void OnReverse(object? sender, RoutedEventArgs e) => Reverse();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnApply(object? sender, RoutedEventArgs e) => Close(CurrentOrder);
}
