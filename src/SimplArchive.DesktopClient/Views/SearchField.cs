using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Projektanker.Icons.Avalonia;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Makes a <see cref="TextBox"/> clearable: an × inside the field that empties it and leaves the caret there,
/// and <c>Esc</c> as the keyboard equivalent (#503).
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, not an × per field.</b> Only the Search tab had this, and it had it as five lines of
/// inline markup — copy that four more times and the focus fix lands on four of them, never the fifth, and
/// nothing points that out. So it is an attached property: <c>&lt;TextBox v:SearchField.Clearable="True" /&gt;</c>.
/// </para>
/// <para>
/// <b>Clearing restores focus</b>, which is the whole point. Without it the user clicks ×, then has to click the
/// field again before typing — a one-click action costing three, which is worse than select-all-and-delete.
/// </para>
/// <para>
/// <b>The button hides itself when the field is empty.</b> A permanently-visible × on an empty field is a control
/// that cannot do anything, which is noise hiding the one that can (ADR 0550). It is not a hover-only affordance
/// either: touch has no hover (ADR 0491), so it appears whenever there is text, however the text arrived.
/// </para>
/// <para>
/// <b>Esc is handled only when there is something to clear</b>, and it tunnels so it wins before anything else
/// looks at the key. An empty field lets Esc straight through, so it cannot swallow "Esc closes the dialog" or
/// "Esc leaves full-screen" — the gesture keeps its meaning everywhere it already had one (ADR 0550).
/// </para>
/// </remarks>
public static class SearchField
{
    /// <summary>Set to <c>true</c> on a search or filter <see cref="TextBox"/> to give it the × and Esc.</summary>
    /// <remarks>The name is deliberately not <c>IsClearable</c>: the Search tab's box is <c>x:Name="SearchBox"</c>,
    /// and a type of that name would collide with the field the namescope generates for it.</remarks>
    public static readonly AttachedProperty<bool> ClearableProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Clearable", typeof(SearchField));

    public static void SetClearable(TextBox box, bool value) => box.SetValue(ClearableProperty, value);

    public static bool GetClearable(TextBox box) => box.GetValue(ClearableProperty);

    static SearchField() => ClearableProperty.Changed.AddClassHandler<TextBox>(OnClearableChanged);

    private static void OnClearableChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            box.InnerRightContent = BuildClearButton(box);
            box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }
        else
        {
            box.InnerRightContent = null;
            box.RemoveHandler(InputElement.KeyDownEvent, (EventHandler<KeyEventArgs>)OnKeyDown);
        }
    }

    private static Button BuildClearButton(TextBox box)
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = default,
            Padding = new Thickness(4, 0),
            Focusable = false, // Tab should reach the next field, not a button that only undoes typing.
            Content = new Icon { Value = "mdi-close", FontSize = 14 },
        };

        ToolTip.SetTip(button, Strings.Get("SearchClearText"));
        button.Bind(Visual.IsVisibleProperty, new Binding(nameof(TextBox.Text))
        {
            Source = box,
            Converter = StringConverters.IsNotNullOrEmpty,
        });
        button.Click += (_, _) => Clear(box);
        return button;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is TextBox { Text.Length: > 0 } box)
        {
            e.Handled = true;
            Clear(box);
        }
    }

    // Assigning Text (rather than calling a view-model command) is what keeps this one implementation: every
    // field binds its own property, and TextBox.Text propagates on change, so the binding carries it back.
    private static void Clear(TextBox box)
    {
        box.Text = string.Empty;
        box.Focus();
    }
}
