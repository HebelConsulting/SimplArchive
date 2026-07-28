using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the desktop Manage-access dialog (ADR "Manage-access UI for document/folder ACLs"): lists the item's own
// ACL grants, adds/edits via Viewer/Editor/Manager presets + an Advanced nine-right checklist, and removes.
// Self-gating: a 403 on load (the caller lacks CanManagePermissions) shows a read-only message instead of an
// editor. Mirrors the web ManageAccessDialog.
public sealed partial class ManageAccessViewModel : ObservableObject
{
    private SimplArchiveApiClient? _api;
    private Guid _documentId;
    private List<SimplArchiveApiClient.GrantablePrincipalInfo> _principals = [];

    [ObservableProperty] private string _documentName = "";
    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private bool _forbidden;
    [ObservableProperty] private bool _loadFailed;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(InheritanceText))] private bool _breaksInheritance;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasStatus))] private string _status = "";
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public ObservableCollection<AclRowViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;
    public bool Ready => !Loading && !Forbidden && !LoadFailed;
    public string InheritanceText => BreaksInheritance ? Strings.Get("MaAccessOwn") : Strings.Get("MaAccessInherited");

    // Confirm provider (nested ConfirmDialog owned by the dialog window) set by the dialog code-behind.
    public Func<string, Task<bool>>? ConfirmRemoveAsync { get; set; }

    // ---- editor state ----
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EditorVisible))][NotifyPropertyChangedFor(nameof(AddButtonEnabled))] private bool _editing;
    [ObservableProperty] private bool _isNew;
    public ObservableCollection<GranteeOption> AvailablePrincipals { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private GranteeOption? _selectedPrincipal;
    [ObservableProperty] private string _editingPrincipalLabel = "";
    private string _editingPrincipalType = "";
    private Guid _editingPrincipalId;

    // The nine rights as bindable checkboxes.
    [ObservableProperty] private bool _canSee;
    [ObservableProperty] private bool _canReadContent;
    [ObservableProperty] private bool _canEditContent;
    [ObservableProperty] private bool _canEditIndexData;
    [ObservableProperty] private bool _canCreateSubItems;
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private bool _canMove;
    [ObservableProperty] private bool _canAnnotate;
    [ObservableProperty] private bool _canManagePermissions;

    [ObservableProperty] private string _presetLabel = "";

    public bool EditorVisible => Editing;
    public bool AddButtonEnabled => !Editing && AvailablePrincipals.Count > 0;
    public bool CanSave => (!IsNew || SelectedPrincipal is not null) && AnyRight();

    // Recompute the derived role label + Save-enablement whenever a right toggles.
    partial void OnCanSeeChanged(bool value) => OnRightsChanged();
    partial void OnCanReadContentChanged(bool value) => OnRightsChanged();
    partial void OnCanEditContentChanged(bool value) => OnRightsChanged();
    partial void OnCanEditIndexDataChanged(bool value) => OnRightsChanged();
    partial void OnCanCreateSubItemsChanged(bool value) => OnRightsChanged();
    partial void OnCanDeleteChanged(bool value) => OnRightsChanged();
    partial void OnCanMoveChanged(bool value) => OnRightsChanged();
    partial void OnCanAnnotateChanged(bool value) => OnRightsChanged();
    partial void OnCanManagePermissionsChanged(bool value) => OnRightsChanged();

    private void OnRightsChanged()
    {
        PresetLabel = Strings.Get(PresetLabelKey(Current()));
        OnPropertyChanged(nameof(CanSave));
    }

    public async Task SetupAsync(SimplArchiveApiClient api, Guid documentId, string documentName)
    {
        _api = api;
        _documentId = documentId;
        DocumentName = documentName;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        Loading = true;
        Forbidden = false;
        LoadFailed = false;
        try
        {
            var info = await _api!.GetAclAsync(_documentId);
            Forbidden = info.Forbidden;
            if (Forbidden)
            {
                return;
            }

            BreaksInheritance = info.BreaksInheritance;
            _principals = info.Principals;
            Entries.Clear();
            foreach (var e in info.Entries)
            {
                Entries.Add(new AclRowViewModel(e.PrincipalType, e.PrincipalId, PrincipalName(e.PrincipalType, e.PrincipalId), TypeLabel(e.PrincipalType), e.Rights, this));
            }
            RefreshAvailable();
        }
        catch (Exception)
        {
            LoadFailed = true;
        }
        finally
        {
            Loading = false;
            NotifyView();
        }
    }

    private void NotifyView()
    {
        OnPropertyChanged(nameof(Ready));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(AddButtonEnabled));
    }

    private void RefreshAvailable()
    {
        AvailablePrincipals.Clear();
        foreach (var p in _principals.Where(p => !Entries.Any(e => e.PrincipalType == p.Type && e.PrincipalId == p.Id)))
        {
            AvailablePrincipals.Add(new GranteeOption(p.Type, p.Id, p.Name));
        }
        OnPropertyChanged(nameof(AddButtonEnabled));
    }

    // ---- editor lifecycle ----

    [RelayCommand]
    private void BeginAdd()
    {
        IsNew = true;
        SelectedPrincipal = AvailablePrincipals.FirstOrDefault();
        Load(ViewerBundle());
        Status = "";
        Editing = true;
    }

    public void BeginEdit(AclRowViewModel row)
    {
        IsNew = false;
        _editingPrincipalType = row.PrincipalType;
        _editingPrincipalId = row.PrincipalId;
        EditingPrincipalLabel = $"{row.PrincipalLabel} ({row.TypeLabel})";
        Load(row.Rights);
        Status = "";
        Editing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editing = false;
        Status = "";
    }

    [RelayCommand]
    private void ApplyViewer() => Load(ViewerBundle());

    [RelayCommand]
    private void ApplyEditor() => Load(EditorBundle());

    [RelayCommand]
    private void ApplyManager() => Load(ManagerBundle());

    [RelayCommand]
    private async Task Save()
    {
        if (_api is null || !CanSave)
        {
            return;
        }

        var (type, id) = IsNew
            ? (SelectedPrincipal!.Type, SelectedPrincipal!.Id)
            : (_editingPrincipalType, _editingPrincipalId);

        try
        {
            await _api.SetAclEntryAsync(_documentId, type, id, Current());
            Editing = false;
            await ReloadAsync();
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("MaLoadFailed");
        }
    }

    public async Task RemoveAsync(AclRowViewModel row)
    {
        if (_api is null)
        {
            return;
        }

        var confirm = ConfirmRemoveAsync;
        if (confirm is not null && !await confirm(string.Format(Strings.Get("MaRemoveConfirm"), row.PrincipalLabel)))
        {
            return;
        }

        try
        {
            await _api.RevokeAclEntryAsync(_documentId, row.PrincipalType, row.PrincipalId);
            if (!IsNew && _editingPrincipalType == row.PrincipalType && _editingPrincipalId == row.PrincipalId)
            {
                Editing = false;
            }
            await ReloadAsync();
        }
        catch (Exception)
        {
            Status = Strings.Get("MaLoadFailed");
        }
    }

    // ---- rights helpers ----

    private SimplArchiveApiClient.AclRights Current() => new(
        CanSee, CanReadContent, CanEditContent, CanEditIndexData, CanCreateSubItems, CanDelete, CanMove, CanAnnotate, CanManagePermissions);

    private void Load(SimplArchiveApiClient.AclRights r)
    {
        CanSee = r.CanSee;
        CanReadContent = r.CanReadContent;
        CanEditContent = r.CanEditContent;
        CanEditIndexData = r.CanEditIndexData;
        CanCreateSubItems = r.CanCreateSubItems;
        CanDelete = r.CanDelete;
        CanMove = r.CanMove;
        CanAnnotate = r.CanAnnotate;
        CanManagePermissions = r.CanManagePermissions;
    }

    private bool AnyRight() =>
        CanSee || CanReadContent || CanEditContent || CanEditIndexData || CanCreateSubItems || CanDelete || CanMove || CanAnnotate || CanManagePermissions;

    private static SimplArchiveApiClient.AclRights ViewerBundle() => new(true, true, false, false, false, false, false, false, false);
    private static SimplArchiveApiClient.AclRights EditorBundle() => new(true, true, true, true, true, false, true, true, false);
    private static SimplArchiveApiClient.AclRights ManagerBundle() => new(true, true, true, true, true, true, true, true, true);

    public static string PresetLabelKey(SimplArchiveApiClient.AclRights r) =>
        r == ViewerBundle() ? "MaRoleViewer"
        : r == EditorBundle() ? "MaRoleEditor"
        : r == ManagerBundle() ? "MaRoleManager"
        : "MaRoleCustom";

    // ---- principal helpers ----

    private string PrincipalName(string type, Guid id) =>
        _principals.FirstOrDefault(p => p.Type == type && p.Id == id)?.Name ?? $"{TypeLabel(type)} {id.ToString()[..8]}…";

    public static string TypeLabel(string type) => type switch
    {
        "users" => Strings.Get("MaTypeUser"),
        "groups" => Strings.Get("MaTypeGroup"),
        "service-accounts" => Strings.Get("MaTypeServiceAccount"),
        _ => type,
    };
}

// One selectable grantable principal in the "Add access" picker.
public sealed record GranteeOption(string Type, Guid Id, string Name);

// One ACL grant row in the Manage-access list. Edit/Remove call back into the parent VM.
public sealed partial class AclRowViewModel : ObservableObject
{
    private readonly ManageAccessViewModel _parent;

    public AclRowViewModel(string principalType, Guid principalId, string principalLabel, string typeLabel, SimplArchiveApiClient.AclRights rights, ManageAccessViewModel parent)
    {
        PrincipalType = principalType;
        PrincipalId = principalId;
        PrincipalLabel = principalLabel;
        TypeLabel = typeLabel;
        Rights = rights;
        _parent = parent;
    }

    public string PrincipalType { get; }
    public Guid PrincipalId { get; }
    public string PrincipalLabel { get; }
    public string TypeLabel { get; }
    public SimplArchiveApiClient.AclRights Rights { get; }

    public string RoleLabel => Strings.Get(ManageAccessViewModel.PresetLabelKey(Rights));

    [RelayCommand]
    private void Edit() => _parent.BeginEdit(this);

    [RelayCommand]
    private Task Remove() => _parent.RemoveAsync(this);
}
