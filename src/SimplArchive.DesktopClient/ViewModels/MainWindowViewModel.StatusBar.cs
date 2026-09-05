using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

// The status line doubles as the error line, so it distinguishes the two: an ERROR earns a copy button — a
// kiosk user needs to paste the message into a report — while a normal status ("filed", "Not logged in.") does
// not. Errors are set through ReportError, which raises StatusIsError; any ordinary Status change lowers it
// again (OnStatusChanged), so the button appears only for the message that warrants it. The clipboard is the
// view's to reach (ADR 0730), so it arrives as a callback rather than a toolkit dependency here.
public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _statusIsError;

    partial void OnStatusChanged(string value) => StatusIsError = false;

    // Report an error into the status line and flag it. Order matters: assigning Status runs OnStatusChanged
    // (which clears the flag), so the flag is raised AFTER the assignment.
    private void ReportError(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    // Supplied by the view (ADR 0730): copying the status text needs a toolkit clipboard the view-model holds none of.
    public Func<string, Task>? CopyToClipboard { get; set; }

    [RelayCommand]
    private async Task CopyStatusAsync()
    {
        if (Status is { Length: > 0 } text && CopyToClipboard is { } copy)
        {
            await copy(text);
        }
    }
}
