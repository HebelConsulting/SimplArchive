using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Asks which occurrences an edit to a repeating entry should change (#1133).
/// </summary>
/// <remarks>
/// <para>
/// Shown BEFORE the change is sent. The three answers write different things — an EXDATE plus a new entry, a
/// split, or a rewrite — so asking afterwards would be asking whether to undo something already done.
/// </para>
/// <para>
/// It returns the wire value the server expects rather than an enum of its own: the vocabulary is the API's,
/// and a second spelling on this side is a mapping that can drift.
/// </para>
/// </remarks>
public partial class EditScopeDialog : Window
{
    public EditScopeDialog() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>The scope as the API names it, or null when the dialog was cancelled.</summary>
    public string? Scope { get; private set; }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Scope = Following.IsChecked == true
            ? "following"
            : All.IsChecked == true ? "all" : "this";
        Close(Scope);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>
    /// The scope for an edit, or null to abandon it — and null WITHOUT asking when nothing repeats.
    /// </summary>
    /// <remarks>
    /// A one-off entry has one occurrence, so there is nothing to choose and a dialog would be a question with
    /// one answer. The caller sends no scope at all in that case, which is what every client did before this
    /// existed.
    /// </remarks>
    public static async System.Threading.Tasks.Task<string?> AskAsync(Window owner, bool repeats)
    {
        if (!repeats)
        {
            return null;
        }

        return await new EditScopeDialog().ShowDialog<string?>(owner);
    }
}
