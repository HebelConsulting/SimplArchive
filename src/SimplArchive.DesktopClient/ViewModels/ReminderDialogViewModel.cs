using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Remind… dialog (ADR "Document reminders"): set / list / cancel reminders on a document.
// Interactive — does its own API calls via the SimplArchiveApiClient it's constructed with.
public partial class ReminderDialogViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;
    private readonly Guid _documentId;

    public ReminderDialogViewModel(SimplArchiveApiClient api, Guid documentId, string documentName)
    {
        _api = api;
        _documentId = documentId;
        DocumentName = documentName;
    }

    public string DocumentName { get; }

    public string[] RecurrenceOptions { get; } = ["Doesn't repeat", "Daily", "Weekly", "Monthly"];

    [ObservableProperty] private DateTimeOffset? _reminderDate = DateTimeOffset.Now.AddDays(1);
    [ObservableProperty] private TimeSpan? _reminderTime = new(9, 0, 0);
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private int _recurrenceIndex;
    [ObservableProperty] private SimplArchiveApiClient.UserOptionInfo? _selectedTarget;
    [ObservableProperty] private string _status = "";

    // The target picker: "Myself" (Id = Empty) first, then the tenant's active users.
    public ObservableCollection<SimplArchiveApiClient.UserOptionInfo> Targets { get; } = [];
    public ObservableCollection<SimplArchiveApiClient.ReminderInfo> Reminders { get; } = [];

    public async Task LoadAsync()
    {
        Targets.Clear();
        Targets.Add(new SimplArchiveApiClient.UserOptionInfo(Guid.Empty, "Myself"));
        try
        {
            foreach (var t in await _api.GetReminderTargetsAsync(_documentId))
            {
                Targets.Add(t);
            }
        }
        catch (Exception)
        {
            // best-effort
        }

        SelectedTarget = Targets[0];
        await ReloadRemindersAsync();
    }

    private async Task ReloadRemindersAsync()
    {
        Reminders.Clear();
        try
        {
            foreach (var r in await _api.GetRemindersAsync(_documentId))
            {
                Reminders.Add(r);
            }
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (ReminderDate is not { } date)
        {
            return;
        }

        try
        {
            var when = new DateTimeOffset(date.Date + (ReminderTime ?? TimeSpan.Zero), date.Offset);
            var targetId = SelectedTarget is { } t && t.Id != Guid.Empty ? t.Id : (Guid?)null;
            await _api.CreateReminderAsync(_documentId, when, string.IsNullOrWhiteSpace(Note) ? null : Note, RecurrenceIndex, targetId);
            Note = "";
            Status = Strings.Get("StReminderSet");
            await ReloadRemindersAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }

    [RelayCommand]
    private async Task CancelReminderAsync(Guid reminderId)
    {
        try
        {
            await _api.CancelReminderAsync(_documentId, reminderId);
            await ReloadRemindersAsync();
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
    }
}
