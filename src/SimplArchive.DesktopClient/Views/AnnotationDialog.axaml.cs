using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace SimplArchive.DesktopClient.Views;

// View / create / edit a sticky note (ADR "Document annotations"). ShowDialog<AnnotationDialog.Result?> returns
// the action (save/delete) + the edited text/colour, or null if cancelled. The caller (the PreviewViewModel via
// MainWindow's dialog provider) performs the API call.
public partial class AnnotationDialog : Window
{
    // The fixed palette (hex, matches the server's #RRGGBB validation).
    private static readonly string[] Palette = ["#FFEB3B", "#8BC34A", "#4FC3F7", "#FF8A80", "#FFB74D", "#CE93D8"];

    private string _color = "#FFEB3B";
    private readonly bool _canEdit;
    // A markup shape (highlight/rectangle/arrow) rather than a sticky note — its text is optional (ADR
    // "Annotation markup"), so a colour-only change must be savable with an empty text box (the original bug:
    // the empty-text guard made the palette unusable for highlights).
    private readonly bool _isShape;

    public AnnotationDialog() : this("", "#FFEB3B", null, canEdit: true, canDelete: false)
    {
    }

    public AnnotationDialog(string text, string color, string? authorName, bool canEdit, bool canDelete, bool isShape = false)
    {
        InitializeComponent();

        _canEdit = canEdit;
        _isShape = isShape;
        _color = string.IsNullOrWhiteSpace(color) ? "#FFEB3B" : color;
        NoteBox.Text = text;
        NoteBox.IsReadOnly = !canEdit;
        SaveButton.IsVisible = canEdit;
        DeleteButton.IsVisible = canDelete;

        if (isShape)
        {
            Title = "Markup";
            NoteLabel.Text = "Label (optional)";
        }

        if (!string.IsNullOrWhiteSpace(authorName))
        {
            AuthorText.Text = $"By {authorName}";
            AuthorText.IsVisible = true;
        }

        BuildSwatches();
        Opened += (_, _) => { if (canEdit) NoteBox.Focus(); };
    }

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Avalonia.Thickness(0),
                Background = Brush.Parse(hex),
                Tag = hex,
                IsEnabled = _canEdit,
                BorderThickness = new Avalonia.Thickness(hex == _color ? 3 : 1),
                BorderBrush = hex == _color ? Brushes.DodgerBlue : Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            swatch.Click += OnColor;
            Swatches.Children.Add(swatch);
        }
    }

    private void OnColor(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            _color = hex;
            BuildSwatches(); // redraw the selection outline
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var text = (NoteBox.Text ?? "").Trim();
        if (!CanSave(text, _isShape))
        {
            return; // a sticky note requires text; a markup shape's text is optional (colour-only save is fine)
        }

        Close(new Result("save", text, _color));
    }

    private void OnDelete(object? sender, RoutedEventArgs e) => Close(new Result("delete", (NoteBox.Text ?? "").Trim(), _color));

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    // A sticky note requires text; a markup shape's text is optional, so a colour-only save is valid for a shape
    // (ADR "Annotation shape recolour"). Pure, so it's headlessly testable (`--annotation-save-test`).
    public static bool CanSave(string? text, bool isShape) => isShape || !string.IsNullOrWhiteSpace(text);

    public sealed record Result(string Action, string Text, string Color);
}
