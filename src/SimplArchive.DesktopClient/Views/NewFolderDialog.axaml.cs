using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Small modal dialog to enter a new folder name. ShowDialog<string?> returns the trimmed name, or null if
// cancelled/empty. See ADR "Desktop new-folder".
//
// It COMPLETES as you type when the caller supplies a populator (ABI 0.21) — for the masks whose name is an
// identifier rather than a label, a weather folder called LSZH, where the user was typing four letters from
// memory and a wrong one silently fetches nothing.
//
// It remains a text box that suggests, never a picker: CLAUDE.md's standing principle is that a value the
// user already knows is TYPED, with a chooser offered beside the typing rather than imposed on it. So the
// dialog closes with whatever is in the box — a name matching no suggestion is accepted, because completion
// here is a convenience and validation belongs to whoever acts on the name.
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
            // Enter accepts what is TYPED. The dropdown handles its own Enter while an item is highlighted,
            // so this only ever sees the case where the user has finished typing rather than choosing.
            if (e.Key == Key.Enter)
            {
                Accept();
            }
        };
    }

    /// <summary>
    /// Turns this into a completing name box: the callback is asked for suggestions as the user types.
    /// </summary>
    /// <remarks>
    /// A settable hook rather than a constructor argument, deliberately — ADR 0730's test. The dialog is
    /// built by a VIEW that does not exist when the view-model does, and omitting this fails LOUDLY in the
    /// only way that matters: the box simply behaves as it always did, which is a complete and correct
    /// prompt rather than a broken one.
    /// </remarks>
    public void CompleteFrom(Func<string?, CancellationToken, Task<IEnumerable<object>>> populator) =>
        NameBox.AsyncPopulator = populator;

    private void OnCreate(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        var name = NameBox.Text?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }
}

/// <summary>One offered NAME in the create dialog's type-ahead (ABI 0.21).</summary>
/// <remarks>
/// <see cref="ToString"/> is what <c>AutoCompleteBox</c> puts in the box when an item is chosen, so it
/// returns the VALUE — the name the document will actually get — while the template beside it shows the
/// label and the identifying line. Without that override the box would fill with the type name.
/// </remarks>
public sealed record NameSuggestion(string Value, string Label, string? Description)
{
    /// <summary>Whether there is an identifying line to draw under the label.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public override string ToString() => Value;
}
