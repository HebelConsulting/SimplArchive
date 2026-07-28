using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Picks a new "retain until" date when extending a document's retention (ADR "Retention review-before-
// disposition"). ShowDialog<string?> returns the date as "yyyy-MM-dd", or null if cancelled / not in the future.
public partial class ExtendRetentionDialog : Window
{
    public ExtendRetentionDialog() : this("")
    {
    }

    public ExtendRetentionDialog(string documentName)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(documentName))
        {
            Prompt.Text = $"Retain '{documentName}' until:";
        }

        UntilPicker.SelectedDate = DateTimeOffset.Now.AddYears(1);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        // Only a future date is valid (the server also enforces this).
        if (UntilPicker.SelectedDate is { } d && d.Date > DateTimeOffset.Now.Date)
        {
            Close(d.ToString("yyyy-MM-dd"));
        }
        else
        {
            Close(null);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
