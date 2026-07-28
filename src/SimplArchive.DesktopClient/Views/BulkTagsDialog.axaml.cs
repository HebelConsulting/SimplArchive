using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SimplArchive.DesktopClient.Views;

// Builds the tag set to add to every selected document (ADR "Bulk actions on selected documents") — chips + a
// catalog-autocomplete add box. ShowDialog<IReadOnlyList<string>?> returns the normalized tags, or null on
// cancel.
public partial class BulkTagsDialog : Window
{
    private readonly ObservableCollection<string> _tags = [];

    public BulkTagsDialog() : this([])
    {
    }

    public BulkTagsDialog(IReadOnlyList<string> catalog)
    {
        InitializeComponent();
        Chips.ItemsSource = _tags;
        TagBox.ItemsSource = catalog;
    }

    private void OnTagKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter)
        {
            AddCurrent();
        }
    }

    private void OnAddTag(object? sender, RoutedEventArgs e) => AddCurrent();

    private void AddCurrent()
    {
        var t = (TagBox.Text ?? "").Trim().ToLowerInvariant();
        if (t.Length is > 0 and <= 100 && !_tags.Contains(t))
        {
            _tags.Add(t);
        }

        TagBox.Text = "";
    }

    private void OnRemoveTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: string tag })
        {
            _tags.Remove(tag);
        }
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close((IReadOnlyList<string>)_tags.ToList());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
