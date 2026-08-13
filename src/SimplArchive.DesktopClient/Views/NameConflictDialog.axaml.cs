using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// A dropped file whose name is already taken in the target folder.
//
// Before this dialog the file was simply dropped: the create returned 409 and the status line showed a message
// the user had usually stopped looking at, so a drag-and-drop appeared to do nothing. The two things they could
// plausibly have meant are offered instead — this is a newer revision of the document that is already there, or
// it is a different document that happens to share a name — with the filing comment they would otherwise have
// had to add afterwards.
//
// Deliberately no "overwrite": nothing in this product replaces content in place. The nearest true thing is a new
// version, which is the first option. ShowDialog<NameConflictChoice?> returns the choice, or null when cancelled.
public partial class NameConflictDialog : Window
{
    public NameConflictDialog()
    {
        InitializeComponent();
    }

    public NameConflictDialog(UploadConflictResolver.NameConflictRequest request) : this()
    {
        IntroBlock.Text = string.Format(
            Strings.Get(request.CanFileAsVersion ? "NcIntro" : "NcIntroFolder"), request.FileName);
        NewNameBox.Text = request.SuggestedName;

        // A folder can hold the name too — sibling names are unique across folders AND documents — and adding a
        // version to a folder would turn it into a document. The choice is shown disabled with the reason rather
        // than omitted: a user who expected it needs to know why it is gone.
        VersionHint.Text = Strings.Get(request.CanFileAsVersion ? "NcAsVersionHint" : "NcAsVersionFolder");
        VersionChoice.IsEnabled = request.CanFileAsVersion;
        if (!request.CanFileAsVersion)
        {
            VersionChoice.IsChecked = false;
            RenameChoice.IsChecked = true;
        }
    }

    private void OnFile(object? sender, RoutedEventArgs e)
    {
        var rename = RenameChoice.IsChecked == true;
        var newName = (NewNameBox.Text ?? "").Trim();
        if (rename && newName.Length == 0)
        {
            // The one thing the second choice cannot do without. Refusing here rather than disabling the button
            // keeps the reason visible — an inert button explains nothing (ADR 0550).
            IntroBlock.Text = Strings.Get("NcNameRequired");
            return;
        }

        Close(new UploadConflictResolver.NameConflictChoice(
            rename ? "rename" : "version", newName, (CommentBox.Text ?? "").Trim()));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
