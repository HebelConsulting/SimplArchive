using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The Intray tab's ribbon handlers (#519 continues #466's split of this code-behind by feature): the page
// operations -- split, sort, join, deskew, cut-at-patch-codes -- their three sticky auto-preferences, the
// separator sheet, and the row actions delete / send / move-to-mine.
//
// Same class, so nothing needed passing: these read `DataContext`, the tree-context node and the row helpers
// exactly as they did inline. That is the whole argument for a partial over a control of its own here -- the
// handlers are view GLUE, and their logic already lives in the view models they call.
public partial class MainWindow
{
    // ---- Page operations (#487, ADR 0575) -------------------------------------------------------------
    // Each is addressed from the href the server advertised for the SELECTED row, never composed (ADR 0543),
    // and each re-reads that address at the moment of acting rather than trusting pane state (ADR 0559).

    internal void OnIntraySplit(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || ServerIntrayList.SelectedItem is not IntrayItemViewModel item
            || await vm.Intray.Actions.GetPagesAsync(item) is not { SplitHref: { } splitHref } pages)
        {
            return;
        }

        // Splitting adds N items and keeps the source, so the count is worth stating before it happens: on a
        // 40-page scan the difference between "split" and "what have I done" is knowing it was 40.
        var prompt = string.Format(Strings.Get("IntraySplitConfirm"), item.Name, pages.PageCount);
        if (await new ConfirmDialog(prompt, Strings.Get("IntraySplit")).ShowDialog<bool>(this))
        {
            await vm.Intray.Actions.SplitAsync(item, splitHref);
        }
    });

    internal void OnIntraySortPages(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || ServerIntrayList.SelectedItem is not IntrayItemViewModel item
            || item.Item is not { } info
            || vm.Api is not { } api
            || await vm.Intray.Actions.GetPagesAsync(item) is not { SortHref: { } sortHref })
        {
            return;
        }

        var thumbnails = await IntrayPageThumbnails.LoadAsync(api, info);
        if (thumbnails.Count == 0)
        {
            // The loader's contract all along — "the caller then keeps the sort affordance hidden rather than
            // opening a dialog full of blanks" — except the caller didn't, which is what made the scaling
            // crash (#522) present as an empty dialog instead of as this message.
            vm.Status = Strings.Get("IntraySortNoPages");
            return;
        }

        var dialog = new SortPagesDialog(item.Name, thumbnails.Cast<Bitmap?>().ToList());
        if (await dialog.ShowDialog<SortPagesDialog.Result?>(this) is { } arrangement)
        {
            await vm.Intray.Actions.SortAsync(item, sortHref, arrangement.Order, arrangement.Rotations);
        }
    });

    internal void OnIntrayRotateAutoToggled(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && sender is ToggleButton { IsChecked: { } enabled })
        {
            await vm.Intray.Actions.SetRotateAutomaticallyAsync(enabled);
        }
    });

    internal void OnIntrayDeskewAutoToggled(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && sender is ToggleButton { IsChecked: { } enabled })
        {
            await vm.Intray.Actions.SetDeskewAutomaticallyAsync(enabled);
        }
    });

    // Straighten THIS document, now. Addressed from the selected row's own deskew rel, re-read at the moment of
    // acting rather than trusted from pane state (ADR 0559).
    internal void OnIntrayDeskew(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || ServerIntrayList.SelectedItem is not IntrayItemViewModel item
            || await vm.Intray.Actions.GetPagesAsync(item) is not { DeskewHref: { } deskewHref })
        {
            return;
        }

        // The format change is stated before it happens: a TIFF comes back a PDF, because straightening
        // re-renders the pages. Discovering that afterwards is how a user concludes the archive is unreliable.
        var prompt = string.Format(Strings.Get("IntrayDeskewConfirm"), item.Name);
        if (await new ConfirmDialog(prompt, Strings.Get("IntrayDeskewNow")).ShowDialog<bool>(this))
        {
            await vm.Intray.Actions.DeskewAsync(item, deskewHref);
        }
    });

    internal void OnIntrayPatchAutoToggled(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && sender is ToggleButton { IsChecked: { } enabled })
        {
            await vm.Intray.Actions.SetCutAtPatchCodesAutomaticallyAsync(enabled);
        }
    });

    // Cut THIS batch at its separator sheets, now — addressed from the selected row's own rel, re-read at the
    // moment of acting rather than trusted from pane state (ADR 0559).
    internal void OnIntrayPatchCut(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm
            || ServerIntrayList.SelectedItem is not IntrayItemViewModel item
            || await vm.Intray.Actions.GetPagesAsync(item) is not { PatchCodesHref: { } patchCodesHref })
        {
            return;
        }

        // What happens to the batch is stated before it happens: it stays, under a name that says it can go.
        // A user who expects it to vanish and finds it still there concludes the cut did not work.
        var prompt = string.Format(Strings.Get("IntrayPatchCutConfirm"), item.Name);
        if (await new ConfirmDialog(prompt, Strings.Get("IntrayPatchCutNow")).ShowDialog<bool>(this))
        {
            await vm.Intray.Actions.CutAtPatchCodesAsync(item, patchCodesHref);
        }
    });

    // The separator sheet itself, opened in whatever the OS prints PDFs with. Nothing else in the app can
    // substitute for this step: without a printed sheet there is nothing to cut at.
    internal void OnIntrayPatchSheet(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.Intray.Actions.OpenPatchCodeSheetAsync();
        }
    });

    internal void OnIntrayJoin(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var names = (ServerIntrayList.SelectedItems ?? new List<object>())
            .OfType<IntrayItemViewModel>().Select(i => i.Name).ToList();
        if (names.Count < 2)
        {
            return;
        }

        if (await new JoinItemsDialog(names).ShowDialog<JoinItemsDialog.Result?>(this) is { } result)
        {
            await vm.Intray.Actions.JoinAsync(result.Names, result.Name);
        }
    });



    // A row-tagged call deletes THAT row (ADR 0559); the ribbon (no Tag) composes across the whole
    // multi-selection — N deletes, one confirm naming the count (the checkout bulk story; review finding).
    internal void OnIntrayDelete(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var items = (sender as Control)?.Tag is IntrayItemViewModel tagged
            ? new List<IntrayItemViewModel> { tagged }
            : ServerIntrayList.SelectedItems?.OfType<IntrayItemViewModel>().ToList() ?? [];
        if (items.Count == 0)
        {
            return;
        }

        var message = items.Count == 1
            ? $"Delete '{items[0].Name}' from the intray?"
            : string.Format(Strings.Get("IntrayDeleteManyConfirm"), items.Count);
        if (await new ConfirmDialog(message, "Delete").ShowDialog<bool>(this))
        {
            foreach (var item in items)
            {
                await vm.Intray.Actions.DeleteAsync(item);
            }
        }
    });

    // "Send to…" (ADR 0532): hand an own item to a chosen group or user via the picker dialog.
    internal void OnIntraySend(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is not MainWindowViewModel vm || IntrayItemFrom(sender) is not { } item)
        {
            return;
        }

        var targets = await vm.Intray.Actions.GetSendTargetsAsync();
        if (await new SendToIntrayDialog(item.Name, targets).ShowDialog<IntrayApi.IntrayTargetInfo?>(this) is { } target)
        {
            await vm.Intray.Actions.SendAsync(item, target);
        }
    });

    // "Move to my intray" (ADR 0532): claim a group / other-user item into my own intray.
    internal void OnIntrayMoveToMine(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        if (DataContext is MainWindowViewModel vm && IntrayItemFrom(sender) is { } item)
        {
            await vm.Intray.Actions.ClaimToMineAsync(item);
        }
    });
}
