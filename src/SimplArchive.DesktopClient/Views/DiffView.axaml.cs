using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

public partial class DiffView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<DiffRowViewModel>?> RowsSourceProperty =
        AvaloniaProperty.Register<DiffView, IReadOnlyList<DiffRowViewModel>?>(nameof(RowsSource));

    public DiffView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<DiffRowViewModel>? RowsSource
    {
        get => GetValue(RowsSourceProperty);
        set => SetValue(RowsSourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsSourceProperty)
        {
            Rows.ItemsSource = RowsSource;
        }
    }
}
