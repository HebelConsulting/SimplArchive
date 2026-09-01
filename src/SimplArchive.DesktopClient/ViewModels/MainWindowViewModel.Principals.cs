using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The Users & groups tab: who exists, what they may do, and the per-principal state that tab shows. The
// principal list and the rights matrix, group membership, the profile photo, the MFA and IMAP status lines,
// impersonation, passwords, and creating or deactivating a principal.
//
// One subject, though it did not look like one: it was spread over FOUR headings, and the photo was split
// between two of them six hundred lines apart -- its STATE under "Profile photo", its load/set/remove
// OPERATIONS under "Passwords". Two of the four banners had stopped describing what followed them, which is
// the pattern issue #941 records for this file: inserting a member above a comment moves neither, so in a
// file of five thousand lines the headings decay silently and name whatever was written first. "Passwords"
// was true of its first 48 lines and of nothing for the 286 after it.
//
// A partial rather than a type of its own: these read and write this view model's own observable state, and
// the tab's controls bind to them by name -- so a separate type would need the state passed to it and the
// bindings re-pointed, to buy nothing a partial does not already give.
public sealed partial class MainWindowViewModel
{
    // ---- Users & groups administration (ADR "Users & groups administration tab") ---------------------

    // Gates the Users & groups tab (set from whoami on login); true for a tenant admin / CanManageUsers holder.
    [ObservableProperty] private bool _canManageUsers;
    [ObservableProperty] private bool _canManageServiceAccounts;

    public ObservableCollection<PrincipalRowViewModel> Principals { get; } = [];
    public ObservableCollection<PrincipalRightViewModel> PrincipalRights { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPrincipal))]
    private PrincipalRowViewModel? _selectedPrincipal;

    public bool HasSelectedPrincipal => SelectedPrincipal is not null;

    [ObservableProperty] private string _principalRightsHeader = string.Empty;
    [ObservableProperty] private bool _selectedPrincipalIsGroup;
    [ObservableProperty] private bool _ugBusy;

    // The rights matrix is read-only until Edit is clicked; then Save/Cancel show (Cancel reverts). Mirrors the
    // Repositories detail pane's single-edit toggle and the web tab (ADR "Desktop recycle bin parity").
    [ObservableProperty] private bool _ugEditingRights;

    [RelayCommand]
    private void BeginRightsEdit() => UgEditingRights = true;

    [RelayCommand]
    private void CancelRightsEdit()
    {
        UgEditingRights = false;
        RebuildRightsMatrix(SelectedPrincipal); // discard unsaved checkbox changes
        if (SelectedPrincipal is { } p) SelectedPrincipalClearance = p.Rights.ClearanceRank;
    }

    private void RebuildRightsMatrix(PrincipalRowViewModel? value)
    {
        PrincipalRights.Clear();
        if (value is null)
        {
            return;
        }

        for (var i = 0; i < SystemRightsMatrix.Labels.Length; i++)
        {
            PrincipalRights.Add(new PrincipalRightViewModel(SystemRightsMatrix.Labels[i], SystemRightsMatrix.At(value.Rights, i)));
        }
    }

    async partial void OnSelectedPrincipalChanged(PrincipalRowViewModel? value)
    {
        UgEditingRights = false; // selecting a principal exits edit mode
        PrincipalRights.Clear();
        if (value is null)
        {
            PrincipalRightsHeader = string.Empty;
            return;
        }

        SelectedPrincipalIsGroup = value.IsGroup;
        SelectedPrincipalIsUser = !value.IsGroup;
        OnPropertyChanged(nameof(SelectedPrincipalMfaStatus));
        OnPropertyChanged(nameof(SelectedPrincipalImapStatus));
        OnPropertyChanged(nameof(CanResetSelectedPrincipalMfa));
        OnPropertyChanged(nameof(CanImpersonateSelectedPrincipal));
        PrincipalRightsHeader = $"{value.Name} — {(value.IsGroup ? "group" : "user")} rights";
        RebuildRightsMatrix(value);
        SelectedPrincipalClearance = value.Rights.ClearanceRank;

        await LoadSelectedPrincipalPhotoAsync(value);
        await LoadGroupMembersAsync(value);
    }

    // ---- Group membership (ADR "Group membership editing") ------------------------------------------

    public ObservableCollection<UserOptionInfo> GroupMembers { get; } = [];
    public ObservableCollection<UserOptionInfo> MemberCandidates { get; } = [];

    [ObservableProperty] private bool _hasGroupMembers;

    // The AutoCompleteBox's selected candidate — setting it (a pick) adds that user, then resets.
    [ObservableProperty] private UserOptionInfo? _selectedMemberToAdd;

    async partial void OnSelectedMemberToAddChanged(UserOptionInfo? value)
    {
        if (value is null || _api is null || SelectedPrincipal is not { IsGroup: true } group)
        {
            return;
        }

        try
        {
            await _api.Admin.AddGroupMemberAsync(group.Source!, value.Id);
            await LoadGroupMembersAsync(group);
            Status = string.Format(Strings.Get("StAdded"), value.DisplayName);
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrAddMember");
        }

        SelectedMemberToAdd = null; // reset the picker for the next add
    }

    private async Task LoadGroupMembersAsync(PrincipalRowViewModel? p)
    {
        GroupMembers.Clear();
        MemberCandidates.Clear();
        HasGroupMembers = false;
        if (_api is null || p is null || !p.IsGroup)
        {
            return;
        }

        try
        {
            foreach (var m in await _api.Admin.GetGroupMembersAsync(p.Source!))
            {
                GroupMembers.Add(m);
            }

            HasGroupMembers = GroupMembers.Count > 0;
            RebuildMemberCandidates();
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadMembers");
        }
    }

    // Non-member tenant users, for the add-picker.
    private void RebuildMemberCandidates()
    {
        MemberCandidates.Clear();
        var memberIds = GroupMembers.Select(m => m.Id).ToHashSet();
        foreach (var p in Principals.Where(p => !p.IsGroup && !memberIds.Contains(p.Id)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            MemberCandidates.Add(new UserOptionInfo(p.Id, p.Name));
        }
    }

    [RelayCommand]
    private async Task RemoveMember(UserOptionInfo? member)
    {
        if (member is null || _api is null || SelectedPrincipal is not { IsGroup: true } group)
        {
            return;
        }

        try
        {
            await _api.Admin.RemoveGroupMemberAsync(member);
            GroupMembers.Remove(member);
            HasGroupMembers = GroupMembers.Count > 0;
            RebuildMemberCandidates();
            Status = string.Format(Strings.Get("StRemovedName"), member.DisplayName);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRemoveMember");
        }
    }

    // ---- Profile photo (ADR "User profile photo") ---------------------------------------------------

    [ObservableProperty] private bool _selectedPrincipalIsUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPrincipalPhoto))]
    private Bitmap? _selectedPrincipalPhoto;

    public bool HasSelectedPrincipalPhoto => SelectedPrincipalPhoto is not null;

    public string SelectedPrincipalInitials => Initials(SelectedPrincipal?.Name);

    // ---- Two-factor (ADR "MFA (interactive login, TOTP)") -------------------------------------------
    // The current user's MFA status (from whoami) gates the account-menu Enable/Disable items; CanResetMfa
    // gates the admin reset on the selected user.
    [ObservableProperty] private bool _mfaEnabled;
    [ObservableProperty] private bool _canResetMfa;

    // Exposed for the MFA setup dialog, which drives enroll/enable interactively against the API.
    public SimplArchiveApiClient? Api => _api;

    public string SelectedPrincipalMfaStatus => SelectedPrincipal is { IsGroup: false } p
        ? $"Two-factor: {(p.MfaEnabled ? "enabled" : "off")}"
        : "";

    /// <summary>The user's IMAP view preference, read-only (#793): self-service, shown for the admin's answer
    /// to "why does my colleague see the PDFs and I don't".</summary>
    public string SelectedPrincipalImapStatus => SelectedPrincipal is { IsGroup: false } q
        ? string.Format(Strings.Get("UgImapShowAll"), Strings.Get(q.Source?.ImapShowAllDocuments == true ? "UgImapAllDocs" : "UgImapMailOnly"))
        : "";

    public bool CanResetSelectedPrincipalMfa => CanResetMfa && SelectedPrincipal is { IsGroup: false, MfaEnabled: true };

    // Impersonate shows where the row's `impersonate` rel says the server will accept it — not on the row's
    // Rights, which are DIRECT columns while the endpoint refuses on EFFECTIVE ones (#875, ADR 0725).
    public bool CanImpersonateSelectedPrincipal => CanImpersonate && !IsImpersonating
        && SelectedPrincipal is { IsGroup: false, Source: { } src } && src.Href("impersonate") is not null;

    // Called after the user finishes the enroll dialog — reflects the new state in the account menu.
    public void MarkMfaEnabled() => MfaEnabled = true;

    public async Task DisableMyMfaAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Profile.DisableMfaAsync();
            MfaEnabled = false;
            Status = Strings.Get("StMfaDisabled");
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrDisableMfa");
        }
    }

    public async Task ResetSelectedUserMfaAsync()
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        try
        {
            await _api.Admin.ResetUserMfaAsync(p.Source!);
            p.MfaEnabled = false;
            OnPropertyChanged(nameof(SelectedPrincipalMfaStatus));
            OnPropertyChanged(nameof(SelectedPrincipalImapStatus));
            OnPropertyChanged(nameof(CanResetSelectedPrincipalMfa));
            Status = string.Format(Strings.Get("StMfaResetFor"), p.Name);
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrResetMfa");
        }
    }

    // ---- Passwords (ADR "User password management") — the dialogs live in the view; the VM does the API.

    public async Task ChangeMyPasswordAsync(string current, string newPassword)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Profile.ChangeMyPasswordAsync(current, newPassword);
            Status = Strings.Get("StPwChanged");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrChangePw");
        }
    }

    // Returns the generated password (shown once by the view), or null on failure.
    public async Task<string?> ResetSelectedUserPasswordAsync()
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return null;
        }

        try
        {
            var password = await _api.Admin.ResetUserPasswordAsync(p.Source!);
            Status = string.Format(Strings.Get("StPwResetFor"), p.Name);
            return password;
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
            return null;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrResetPw");
            return null;
        }
    }

    private async Task LoadMyPhotoAsync()
    {
        ProfilePhoto = null;
        if (_api is null || _currentUserId is not { } id)
        {
            return;
        }

        try
        {
            // My own avatar is a rel on the `me` resource; the id was only ever a way to rebuild the path.
            var bytes = await _api.Profile.GetMyPhotoAsync();
            ProfilePhoto = bytes is null ? null : Decode(bytes);
        }
        catch (Exception)
        {
            ProfilePhoto = null;
        }
    }

    public async Task SetMyPhotoAsync(byte[] png)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            await _api.Profile.SetMyPhotoAsync(png);
            await LoadMyPhotoAsync();
            Status = Strings.Get("StPhotoUpdated");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrUpdatePhoto");
        }
    }

    private async Task LoadSelectedPrincipalPhotoAsync(PrincipalRowViewModel? p)
    {
        SelectedPrincipalPhoto = null;
        OnPropertyChanged(nameof(SelectedPrincipalInitials));
        if (_api is null || p is null || p.IsGroup)
        {
            return;
        }

        try
        {
            var bytes = await _api.Admin.GetUserPhotoAsync(p.Source!);
            SelectedPrincipalPhoto = bytes is null ? null : Decode(bytes);
        }
        catch (Exception)
        {
            SelectedPrincipalPhoto = null;
        }
    }

    public async Task SetSelectedUserPhotoAsync(byte[] png)
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        try
        {
            await _api.Admin.SetUserPhotoAsync(p.Source!, png);
            await LoadSelectedPrincipalPhotoAsync(p);
            Status = Strings.Get("StPhotoUpdated");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrUpdatePhoto");
        }
    }

    public async Task RemoveSelectedUserPhotoAsync()
    {
        if (_api is null || SelectedPrincipal is not { IsGroup: false } p)
        {
            return;
        }

        try
        {
            await _api.Admin.DeleteUserPhotoAsync(p.Source!);
            SelectedPrincipalPhoto = null;
            Status = Strings.Get("StPhotoRemoved");
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrRemovePhoto");
        }
    }

    public async Task LoadPrincipalsAsync()
    {
        if (_api is null)
        {
            return;
        }

        var previousId = SelectedPrincipal?.Id;
        var previousIsGroup = SelectedPrincipal?.IsGroup;
        try
        {
            var groups = await _api.Admin.GetGroupsAsync();
            var users = await _api.Admin.GetUsersAsync();
            Principals.Clear();
            // Groups first (two-person icon), then users, each alphabetical.
            foreach (var p in groups.Concat(users).OrderByDescending(p => p.IsGroup).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Principals.Add(new PrincipalRowViewModel(p.IsGroup, p.Id, p.Name, p.IsActive, p.Rights, p.MfaEnabled, p));
            }

            SelectedPrincipal = Principals.FirstOrDefault(p => p.Id == previousId && p.IsGroup == previousIsGroup);
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrLoadUsers");
        }
    }

    [RelayCommand]
    private Task RefreshPrincipals() => LoadPrincipalsAsync();


    [RelayCommand]
    private async Task SaveRights()
    {
        if (_api is null || SelectedPrincipal is not { } p)
        {
            return;
        }

        UgBusy = true;
        try
        {
            var rights = SystemRightsMatrix.From(PrincipalRights, SelectedPrincipalClearance);
            if (p.IsGroup)
            {
                await _api.Admin.SetRightsAsync(p.Source!, rights);
            }
            else
            {
                await _api.Admin.SetRightsAsync(p.Source!, rights);
            }

            p.Rights = rights;
            UgEditingRights = false;
            Status = Strings.Get("StRightsSaved");
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrSaveRights");
        }
        finally
        {
            UgBusy = false;
        }
    }

    // Called from the view's New/Copy code-behind (the create dialog lives in the view). copyRights carries
    // the source principal's rights for Copy; null for a fresh New.
    public async Task CreatePrincipalAsync(bool isGroup, string name, string email, AdminClient.SystemRightsData? copyRights)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var created = isGroup ? await _api.Admin.CreateGroupAsync(name) : await _api.Admin.CreateUserAsync(email, name);
            if (copyRights is not null)
            {
                // The create response IS the resource, rels included, so Copy applies the source's rights by
                // following the new row's own `rights` rel — no re-fetch, no path rebuilt from an id (ADR 0555).
                await _api.Admin.SetRightsAsync(created, copyRights);
            }

            await LoadPrincipalsAsync();
            SelectedPrincipal = Principals.FirstOrDefault(p => p.IsGroup == isGroup && p.Id == created.Id);
            Status = isGroup ? "Group created." : "User created.";
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrCreate");
        }
    }

    // Deactivating a user who still holds pending review tasks needs a replacement reviewer (ADR "Workflow
    // review reassignment"); the outcome tells the view to prompt for one and retry.
    public enum DeletePrincipalOutcome { Done, NeedsReplacementReviewer, Failed }

    public async Task<DeletePrincipalOutcome> DeleteSelectedPrincipalAsync()
    {
        if (_api is null || SelectedPrincipal is not { } p)
        {
            return DeletePrincipalOutcome.Failed;
        }

        try
        {
            if (p.IsGroup)
            {
                await _api.Admin.DeleteGroupAsync(p.Source!);
            }
            else
            {
                await _api.Admin.DeleteUserAsync(p.Source!);
            }

            Status = p.IsGroup ? "Group deleted." : "User deactivated.";
            SelectedPrincipal = null;
            await LoadPrincipalsAsync();
            return DeletePrincipalOutcome.Done;
        }
        catch (ReviewerHasPendingReviewsException)
        {
            return DeletePrincipalOutcome.NeedsReplacementReviewer; // keep SelectedPrincipal for the retry
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
            return DeletePrincipalOutcome.Failed;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrDelete");
            return DeletePrincipalOutcome.Failed;
        }
    }

    // Candidate replacement reviewers for a deactivation reassignment — active users other than the one being
    // deactivated (the currently-selected principal).
    public IReadOnlyList<(Guid Id, string Name)> ReplacementReviewerCandidates() =>
        SelectedPrincipal is { } p
            ? Principals.Where(x => !x.IsGroup && x.IsActive && x.Id != p.Id).Select(x => (x.Id, x.Name)).ToList()
            : [];

    // Retry the deactivation, handing the user's pending reviews to the chosen replacement.
    public async Task ReassignReviewsAndDeactivateAsync(Guid replacementId)
    {
        if (_api is null || SelectedPrincipal is not { } p)
        {
            return;
        }

        try
        {
            await _api.Admin.DeleteUserAsync(p.Source!, replacementId);
            Status = Strings.Get("StReviewsReassigned");
            SelectedPrincipal = null;
            await LoadPrincipalsAsync();
        }
        catch (ApiActionException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = Strings.Get("StErrReassign");
        }
    }
}
