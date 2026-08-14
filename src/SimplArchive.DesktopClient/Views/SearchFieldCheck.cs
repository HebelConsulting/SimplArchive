using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--searchclear-test</c>: does <see cref="SearchField"/> actually do what it
/// claims (#503)?
/// </summary>
/// <remarks>
/// <para>
/// A screenshot can show the × but not what it DOES, and the caret it has to leave behind is invisible in a
/// bitmap by definition — so the button is clicked and Esc is pressed for real, on a real
/// <see cref="TextBox"/> carrying the real attached property.
/// </para>
/// <para>
/// <b>The negative half is the one that matters:</b> Esc on an EMPTY field must stay unhandled, or the gesture
/// would quietly stop closing dialogs and leaving full-screen everywhere else in the app (ADR 0550).
/// </para>
/// </remarks>
public static class SearchFieldCheck
{
    /// <summary>Runs the check and prints <c>OK</c> or <c>FAILED</c>. Returns true when everything held.</summary>
    public static bool Run()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        var box = new TextBox { Text = "invoice" };
        SearchField.SetClearable(box, true);
        // A window, because Focus() only takes inside a focus scope — and focus is half of what is claimed.
        new Window { Content = box }.Show();

        var button = box.InnerRightContent as Button;
        var visibleWithText = button?.IsVisible == true;

        button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var clearedByButton = box.Text?.Length is null or 0;
        var focusReturned = box.IsFocused;
        var hiddenWhenEmpty = button?.IsVisible == false;

        // Esc with text: cleared AND swallowed. Esc without: left alone for whoever else wants it.
        box.Text = "invoice";
        var withText = new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent };
        box.RaiseEvent(withText);
        var clearedByEsc = box.Text?.Length is null or 0 && withText.Handled;

        var whenEmpty = new KeyEventArgs { Key = Key.Escape, RoutedEvent = InputElement.KeyDownEvent };
        box.RaiseEvent(whenEmpty);
        var escPassedThrough = !whenEmpty.Handled;

        var passed = visibleWithText && clearedByButton && focusReturned
                     && hiddenWhenEmpty && clearedByEsc && escPassedThrough;

        Console.WriteLine($"visibleWithText={visibleWithText} clearedByButton={clearedByButton} focusReturned={focusReturned}");
        Console.WriteLine($"hiddenWhenEmpty={hiddenWhenEmpty} clearedByEsc={clearedByEsc} escPassedThrough={escPassedThrough}");
        Console.WriteLine(passed ? "OK" : "FAILED");
        return passed;
    }
}
