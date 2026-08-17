using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Enter a new note: a title and a body, in one step (#564). <c>ShowDialog&lt;Result?&gt;</c> returns both, or
/// null if cancelled or the title is empty.
/// </summary>
/// <remarks>
/// The title is required and the body is not, which mirrors what the server enforces: the title becomes both
/// the tree name and the message Subject, so an empty one would produce a note that is unnameable in one place
/// and unidentifiable in the other. An empty body is merely an empty note.
/// </remarks>
public partial class NewNoteDialog : Window
{
    public sealed record Result(string Title, string Body);

    public NewNoteDialog()
    {
        InitializeComponent();
        Opened += (_, _) => TitleBox.Focus();
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        Close(string.IsNullOrEmpty(title) ? null : new Result(title, BodyBox.Text ?? string.Empty));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
