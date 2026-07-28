using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Small modal dialog to enter a new folder name. ShowDialog<string?> returns the trimmed name, or null if
// cancelled/empty. See ADR "Desktop new-folder".
public partial class NewFolderDialog : Window
{
    public NewFolderDialog()
        : this(null, null)
    {
    }

    // Reusable for other "enter a name" prompts (e.g. New repository) by passing a title/label.
    public NewFolderDialog(string? title, string? label)
    {
        InitializeComponent();
        if (title is not null)
        {
            Title = title;
        }

        if (label is not null)
        {
            LabelText.Text = label;
        }

        Opened += (_, _) => NameBox.Focus();
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
            }
        };
    }

    private void OnCreate(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        var name = NameBox.Text?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }
}
