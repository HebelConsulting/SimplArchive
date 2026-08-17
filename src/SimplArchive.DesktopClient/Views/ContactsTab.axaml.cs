using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

// The Contacts tab's view (#564). Its own UserControl rather than more of MainWindow.axaml, which is already
// over the 1000-line limit — the direction issue #519 wants, and the same shape the per-tab ribbons use.
public partial class ContactsTab : UserControl
{
    public ContactsTab() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
