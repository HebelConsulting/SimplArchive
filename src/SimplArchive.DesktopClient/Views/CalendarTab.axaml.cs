using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SimplArchive.DesktopClient.Views;

// The Calendar tab's view (#564), the twin of ContactsTab. Its own UserControl rather than more of
// MainWindow.axaml, which is already over the 1000-line limit — the direction issue #519 wants.
public partial class CalendarTab : UserControl
{
    public CalendarTab() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
