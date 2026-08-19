using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// The structured contact editor (#564, ADR 0631). ShowDialog<ContactEditViewModel?> returns the edited form on
// Save and null on Cancel — the CALLER saves, so this window owns no api client and no failure handling, the
// same split EditProfileDialog uses.
public partial class ContactDialog : Window
{
    // Parameterless ctor for the XAML designer/loader only.
    public ContactDialog() : this(new ContactEditViewModel())
    {
    }

    public ContactDialog(ContactEditViewModel model)
    {
        DataContext = model;
        InitializeComponent();

        // Says which of the two things this window is. The same dialog serves New and Edit (#631), and a
        // create form titled "Edit contact" reads as having opened the wrong record.
        if (model.IsCreate)
        {
            Title = Strings.Get("ContactsNew");
        }

        // An empty card opens with one blank e-mail and phone row rather than nothing at all: a New Contact
        // form whose only visible fields are names reads as not supporting them.
        if (model.Emails.Count == 0)
        {
            model.AddEmail();
        }

        if (model.Phones.Count == 0)
        {
            model.AddPhone();
        }
    }


    /// <summary>
    /// Fetches the raw source the first time the disclosure is opened (#648).
    /// </summary>
    /// <remarks>
    /// Supplied as a lambda by the caller rather than done here: this window owns no api client, which is what
    /// keeps the load and the save testable without a display. Lazy because a card carrying a photo is hundreds
    /// of kilobytes and most edits never open the box.
    /// </remarks>
    public Func<Task>? RawLoader { get; set; }

    private void OnRawExpanding(object? sender, Avalonia.Interactivity.CancelRoutedEventArgs e) =>
        Safe.Fire(async () =>
        {
            if (RawLoader is { } load)
            {
                await load();
            }
        });

    private void OnSave(object? sender, RoutedEventArgs e) => Close(DataContext as ContactEditViewModel);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
