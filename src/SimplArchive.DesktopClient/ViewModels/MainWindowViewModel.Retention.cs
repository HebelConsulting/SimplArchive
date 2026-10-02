using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Retention tab: what is due for disposition, and the two acts that resolve a row (#941).
/// </summary>
/// <remarks>
/// <para>
/// <b>A partial rather than its own type</b>, by ADR 0733's rule as #941 states it: these commands report
/// through the shell's status line and act through its api client, so the cluster cannot stand alone.
/// </para>
/// <para>
/// <b>This banner was the worst-behaved yet, and it is why #941 insists on an inventory first.</b> It claimed
/// 157 lines of "Retention schedule". Retention is the 76 here. Sitting under it were the three upload-conflict
/// DIALOG callbacks and their two records, <c>CreateRepositoryAsync</c>, the signed-in user's display name, and
/// — orphaned by the previous tranche — <c>ClearDetail</c> and <c>HasSelection</c>. Five unrelated concerns
/// under one name, so a reader looking for disposition rules met duplicate-upload handling on the way. Each of
/// them now carries its own banner in the shell rather than travelling here.
/// </para>
/// <para>
/// <c>CanManageClassification</c> deliberately stayed behind: it gates the Retention TAB's visibility, which
/// belongs to the shell that owns the tab strip, exactly as <c>CanViewAuditLog</c> does.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    public ObservableCollection<RetentionRowViewModel> RetentionItems { get; } = [];

    [ObservableProperty] private bool _retentionRequiresReview;

    // The view code-behind provides the "extend retention" date dialog (a native window can't be built here) —
    // a callback rather than a constructor argument, because the window does not exist when this is built
    // (ADR 0730).
    public Func<string, Task<string?>>? ExtendRetentionDialog { get; set; }

    [RelayCommand]
    public async Task LoadRetentionScheduleAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var schedule = await _api.LegalHolds.GetRetentionScheduleAsync();
            RetentionRequiresReview = schedule.RequiresReview;
            RetentionItems.Clear();
            foreach (var item in schedule.Items)
            {
                RetentionItems.Add(new RetentionRowViewModel(item.DocumentId, item.DocumentName, item.RetentionYears, item.DispositionDate, item.Overdue, item.SuspendedByHold, item.RetentionOverrideUntil, item));
            }
        }
        catch (Exception)
        {
            ReportError(Strings.Get("StErrLoadRetention"));
        }
    }

    [RelayCommand]
    private async Task DisposeRetention(RetentionRowViewModel? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.DisposeRetentionAsync(row.Item);
            Status = string.Format(Strings.Get("StDisposed"), row.DocumentName);
            await LoadRetentionScheduleAsync();
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
        }
    }

    [RelayCommand]
    private async Task ExtendRetention(RetentionRowViewModel? row)
    {
        if (_api is null || row is null || ExtendRetentionDialog is null)
        {
            return;
        }

        if (await ExtendRetentionDialog(row.DocumentName) is not { } until)
        {
            return;
        }

        try
        {
            await _api.LegalHolds.ExtendRetentionAsync(row.Item, until);
            Status = string.Format(Strings.Get("StExtendedRetention"), row.DocumentName);
            await LoadRetentionScheduleAsync();
        }
        catch (ApiActionException e)
        {
            ReportError(e.Message);
        }
    }
}
