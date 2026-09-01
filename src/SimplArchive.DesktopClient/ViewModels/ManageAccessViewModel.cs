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

    // The document's ADVERTISED address, taken from the row that opened the dialog at click time (ADR 0559 —
    // the wrong-document grant lived exactly here) — every read in the dialog follows it.
    private string _documentSelfHref = string.Empty;
    private List<DocumentsClient.GrantablePrincipalInfo> _principals = [];

    [ObservableProperty] private string _documentName = string.Empty;
    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private bool _forbidden;
    [ObservableProperty] private bool _loadFailed;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(InheritanceText))][NotifyPropertyChangedFor(nameof(InheritanceToggleLabel))] private bool _breaksInheritance;

    // The advertised acl-inheritance href, or null when the server did not offer it — a repository ROOT has no
    // parent to inherit from, so the toggle can only fail there and is hidden instead (#426, ADR 0543). The
    // indicator line still shows: "this item uses its own permissions" is true and useful on a root.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanToggleInheritance))] private string? _inheritanceHref;

    public bool CanToggleInheritance => InheritanceHref is not null;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasStatus))] private string _status = string.Empty;
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public ObservableCollection<AclRowViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;
    public bool Ready => !Loading && !Forbidden && !LoadFailed;
    public string InheritanceText => BreaksInheritance ? Strings.Get("MaAccessOwn") : Strings.Get("MaAccessInherited");
    public string InheritanceToggleLabel => BreaksInheritance ? Strings.Get("MaRestoreInheritance") : Strings.Get("MaBreakInheritance");

    // Confirm provider (a nested ConfirmDialog owned by the dialog window; message + confirm-button label) set
    // by the dialog code-behind — used by both remove and the inheritance toggle.
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    // ---- effective-access view (ADR 0488): resolved to people, lazy-loaded on expand ----
    [ObservableProperty] private bool _effectiveExpanded;
    [ObservableProperty] private bool _effectiveLoading;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasInheritedFrom))][NotifyPropertyChangedFor(nameof(InheritedFromText))] private string _effectiveInheritedFrom = string.Empty;
    private bool _effectiveLoadedOnce;
    public ObservableCollection<EffectiveRowViewModel> Effective { get; } = [];
    public bool HasInheritedFrom => !string.IsNullOrEmpty(EffectiveInheritedFrom);
    public string InheritedFromText => string.Format(Strings.Get("MaEffInheritedFrom"), EffectiveInheritedFrom);
    public bool EffectiveEmpty => _effectiveLoadedOnce && !EffectiveLoading && Effective.Count == 0;

    partial void OnEffectiveExpandedChanged(bool value)
    {
        if (value && !_effectiveLoadedOnce)
        {
            _ = LoadEffectiveAsync();
        }
    }

    private async Task LoadEffectiveAsync()
    {
        if (_api is null)
        {
            return;
        }

        EffectiveLoading = true;
        try
        {
            var info = await _api.Documents.GetEffectiveAccessAsync(await _api.Documents.RelViaSelfAsync(_documentSelfHref, "acl-entries"));
            EffectiveInheritedFrom = info.InheritedFrom ?? "";
            Effective.Clear();
            foreach (var e in info.Entries.OrderBy(e => e.Type).ThenBy(e => e.Name))
            {
                Effective.Add(new EffectiveRowViewModel(e));
            }
        }
        catch (Exception)
        {
            // Best-effort — the view just stays empty on failure.
        }
        finally
        {
            _effectiveLoadedOnce = true;
            EffectiveLoading = false;
            OnPropertyChanged(nameof(EffectiveEmpty));
        }
    }

    // Refresh the effective view (if already loaded) after a grant/inheritance change.
    private void InvalidateEffective()
    {
        _effectiveLoadedOnce = false;
        if (EffectiveExpanded)
        {
            _ = LoadEffectiveAsync();
        }
    }

    // ---- editor state ----
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EditorVisible))][NotifyPropertyChangedFor(nameof(AddButtonEnabled))] private bool _editing;
    [ObservableProperty] private bool _isNew;
    public ObservableCollection<GranteeOption> AvailablePrincipals { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanSave))] private GranteeOption? _selectedPrincipal;
    [ObservableProperty] private string _editingPrincipalLabel = string.Empty;
    private string _editingPrincipalType = string.Empty;
    private Guid _editingPrincipalId;

    // The entry being edited — carried so Save writes at the address that row advertised (ADR 0555).
    private AclEntryInfo? _editingEntry;

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

    [ObservableProperty] private string _presetLabel = string.Empty;

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

    public async Task SetupAsync(SimplArchiveApiClient api, string documentSelfHref, string documentName)
    {
        _api = api;
        _documentSelfHref = documentSelfHref;
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
            var info = await _api!.Documents.GetAclAsync(_documentSelfHref);
            Forbidden = info.Forbidden;
            if (Forbidden)
            {
                return;
            }

            Grantable = info.GrantableRights;
            BreaksInheritance = info.BreaksInheritance;
            InheritanceHref = info.InheritanceHref;
            _principals = info.Principals;
            Entries.Clear();
            foreach (var e in info.Entries)
            {
                Entries.Add(new AclRowViewModel(e, e.PrincipalType, e.PrincipalId, PrincipalName(e.PrincipalType, e.PrincipalId), TypeLabel(e.PrincipalType), e.Rights, this));
            }
            RefreshAvailable();
            InvalidateEffective();
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
            AvailablePrincipals.Add(new GranteeOption(p.Type, p.Id, p.Name, p));
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
        Status = string.Empty;
        Editing = true;
    }

    public void BeginEdit(AclRowViewModel row)
    {
        IsNew = false;
        _editingPrincipalType = row.PrincipalType;
        _editingPrincipalId = row.PrincipalId;
        _editingEntry = row.Entry;
        EditingPrincipalLabel = $"{row.PrincipalLabel} ({row.TypeLabel})";
        Load(row.Rights);
        Status = string.Empty;
        Editing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editing = false;
        Status = string.Empty;
    }

    [RelayCommand]
    private async Task ToggleInheritance()
    {
        if (_api is null || InheritanceHref is null)
        {
            return;
        }

        var breaking = !BreaksInheritance;
        var confirm = ConfirmAsync;
        var message = breaking ? Strings.Get("MaBreakConfirm") : Strings.Get("MaRestoreConfirm");
        var label = breaking ? Strings.Get("MaBreakInheritance") : Strings.Get("MaRestoreInheritance");
        if (confirm is not null && !await confirm(message, label))
        {
            return;
        }

        try
        {
            await _api.Documents.SetInheritanceAsync(InheritanceHref!, breaking);
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

        // A new grant writes at the principal row's `grant`; an edit at the entry row's `edit`. Either way the
        // address comes from the row, never from the two ids beside it (ADR 0543/0555).
        IAdvertisesLinks? target = IsNew ? SelectedPrincipal?.Principal : _editingEntry;
        if (target is null)
        {
            return;
        }

        try
        {
            await _api.Documents.SetAclEntryAsync(target, Current());
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

        var confirm = ConfirmAsync;
        if (confirm is not null && !await confirm(string.Format(Strings.Get("MaRemoveConfirm"), row.PrincipalLabel), Strings.Get("UgDelete")))
        {
            return;
        }

        try
        {
            await _api.Documents.RevokeAclEntryAsync(row.Entry);
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

    private AclRights Current() => new(
        CanSee, CanReadContent, CanEditContent, CanEditIndexData, CanCreateSubItems, CanDelete, CanMove, CanAnnotate, CanManagePermissions);

    private void Load(AclRights r)
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

    // ---- what THIS caller may confer (#877) ----

    /// <summary>The server's cap, from the ACL collection's `grantableRights` (EffectiveRights.Covers).</summary>
    /// <remarks>
    /// All-false until a load says otherwise, which is both the safe reading and the honest one: before the
    /// answer arrives the caller has not been told they may grant anything (ADR 0543/0559).
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MayGrantSee), nameof(MayGrantReadContent), nameof(MayGrantEditContent))]
    [NotifyPropertyChangedFor(nameof(MayGrantEditIndexData), nameof(MayGrantCreateSubItems), nameof(MayGrantDelete))]
    [NotifyPropertyChangedFor(nameof(MayGrantMove), nameof(MayGrantAnnotate), nameof(MayGrantManagePermissions))]
    [NotifyPropertyChangedFor(nameof(MayApplyViewer), nameof(MayApplyEditor), nameof(MayApplyManager))]
    private AclRights? _grantable;

    public bool MayGrantSee => Grantable?.CanSee ?? false;
    public bool MayGrantReadContent => Grantable?.CanReadContent ?? false;
    public bool MayGrantEditContent => Grantable?.CanEditContent ?? false;
    public bool MayGrantEditIndexData => Grantable?.CanEditIndexData ?? false;
    public bool MayGrantCreateSubItems => Grantable?.CanCreateSubItems ?? false;
    public bool MayGrantDelete => Grantable?.CanDelete ?? false;
    public bool MayGrantMove => Grantable?.CanMove ?? false;
    public bool MayGrantAnnotate => Grantable?.CanAnnotate ?? false;
    public bool MayGrantManagePermissions => Grantable?.CanManagePermissions ?? false;

    /// <summary>A preset is offered only when the caller may grant EVERY right in it.</summary>
    /// <remarks>
    /// Deliberately not "apply the intersection": a button labelled *Manager* that quietly produced something
    /// less than manager would be the lying-state failure of ADR 0724 — the user reads the label, not the nine
    /// checkboxes it moved. Disabled says "not yours to give"; a reduced grant says nothing at all.
    /// </remarks>
    public bool MayApplyViewer => Covers(ViewerBundle());
    public bool MayApplyEditor => Covers(EditorBundle());
    public bool MayApplyManager => Covers(ManagerBundle());

    // The client half of EffectiveRights.Covers, and the only place it is written here: one predicate the three
    // presets share, rather than three hand-expanded conditions that could disagree about the same bundle.
    private bool Covers(AclRights bundle) =>
        (!bundle.CanSee || MayGrantSee)
        && (!bundle.CanReadContent || MayGrantReadContent)
        && (!bundle.CanEditContent || MayGrantEditContent)
        && (!bundle.CanEditIndexData || MayGrantEditIndexData)
        && (!bundle.CanCreateSubItems || MayGrantCreateSubItems)
        && (!bundle.CanDelete || MayGrantDelete)
        && (!bundle.CanMove || MayGrantMove)
        && (!bundle.CanAnnotate || MayGrantAnnotate)
        && (!bundle.CanManagePermissions || MayGrantManagePermissions);

    private static AclRights ViewerBundle() => new(true, true, false, false, false, false, false, false, false);
    private static AclRights EditorBundle() => new(true, true, true, true, true, false, true, true, false);
    private static AclRights ManagerBundle() => new(true, true, true, true, true, true, true, true, true);

    public static string PresetLabelKey(AclRights r) =>
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
// Principal is the catalogue row the server sent — a new grant writes at the `grant` address it advertised
// (ADR 0543/0555), never at one rebuilt from Type + Id.
public sealed record GranteeOption(string Type, Guid Id, string Name, DocumentsClient.GrantablePrincipalInfo Principal);

// One row in the read-only effective-access view (a resolved user, a granted group, or a service account).
public sealed class EffectiveRowViewModel
{
    public EffectiveRowViewModel(DocumentsClient.EffectiveAccessEntryInfo e)
    {
        Name = e.Name;
        TypeLabel = ManageAccessViewModel.TypeLabel(e.Type);
        RoleLabel = Strings.Get(ManageAccessViewModel.PresetLabelKey(e.Rights));
        SourceLabel = e.Access switch
        {
            "admin" => Strings.Get("MaEffAdmin"),
            "group" => string.Format(Strings.Get("MaEffVia"), e.ViaGroup ?? ""),
            _ => Strings.Get("MaEffDirect"),
        };
        Icon = e.Type switch { "groups" => "mdi-account-group", "service-accounts" => "mdi-robot-outline", _ => "mdi-account" };
    }

    public string Name { get; }
    public string TypeLabel { get; }
    public string RoleLabel { get; }
    public string SourceLabel { get; }
    public string Icon { get; }
}

// One ACL grant row in the Manage-access list. Edit/Remove call back into the parent VM.
public sealed partial class AclRowViewModel : ObservableObject
{
    private readonly ManageAccessViewModel _parent;

    // Entry is the row the server sent — edit/remove follow the addresses it advertised (ADR 0543/0555).
    public AclEntryInfo Entry { get; }

    public AclRowViewModel(AclEntryInfo entry, string principalType, Guid principalId, string principalLabel, string typeLabel, AclRights rights, ManageAccessViewModel parent)
    {
        Entry = entry;
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
    public AclRights Rights { get; }

    public string RoleLabel => Strings.Get(ManageAccessViewModel.PresetLabelKey(Rights));

    [RelayCommand]
    private void Edit() => _parent.BeginEdit(this);

    [RelayCommand]
    private Task Remove() => _parent.RemoveAsync(this);
}
