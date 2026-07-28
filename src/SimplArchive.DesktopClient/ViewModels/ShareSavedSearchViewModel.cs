using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// The share-scope dialog for a saved search (ADR "Scoped saved-search sharing"): Private (0) / Everyone (1) /
// Specific (2) with a checkable list of users + groups shown only for Specific. The dialog mutates this VM in
// place; the caller reads back Scope + SelectedPrincipals on OK.
public sealed partial class ShareSavedSearchViewModel : ObservableObject
{
    public ShareSavedSearchViewModel(string name, int scope, IEnumerable<PrincipalOption> principals)
    {
        Name = name;
        _scope = scope;
        foreach (var p in principals)
        {
            Principals.Add(p);
        }
    }

    public string Name { get; }

    [ObservableProperty] private int _scope;

    public ObservableCollection<PrincipalOption> Principals { get; } = [];

    // Bound to the "Specific" panel's visibility.
    public bool ShowPrincipals => Scope == 2;

    partial void OnScopeChanged(int value) => OnPropertyChanged(nameof(ShowPrincipals));

    public IReadOnlyList<(string Type, Guid Id)> SelectedPrincipals =>
        Principals.Where(p => p.IsSelected).Select(p => (p.Type, p.Id)).ToList();

    public sealed partial class PrincipalOption : ObservableObject
    {
        public PrincipalOption(string type, Guid id, string label, bool isSelected)
        {
            Type = type;
            Id = id;
            Label = label;
            _isSelected = isSelected;
        }

        public string Type { get; }
        public Guid Id { get; }
        public string Label { get; }

        [ObservableProperty] private bool _isSelected;
    }
}
