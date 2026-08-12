using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Profile-photo picker with a draggable/resizable square crop (ADR "User profile photo"). ShowDialog<byte[]?>
// returns the cropped 256×256 PNG, or null if cancelled; the caller uploads it.
//
// The crop lives in ProfilePhotoEditor since #464, so "Edit profile" can host it inline rather than stacking a
// second modal. This window is what remains: a title, Save/Cancel, and the return value.
public partial class ProfilePhotoDialog : Window
{
    public ProfilePhotoDialog()
    {
        InitializeComponent();

        // Save stays disabled until there is something to save — the editor says when.
        Editor.ImageChanged += (_, _) => SaveButton.IsEnabled = Editor.HasImage;
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(Editor.CroppedPng());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
