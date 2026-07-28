using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Manage-access ACL UI (ADR "Manage-access UI for document/folder ACLs") end to end via the real desktop api
// client: the demo admin (CanManagePermissions via the IsTenantAdmin bypass) grants a fresh user Viewer access on
// a throwaway folder, reads it back, then revokes it. Exercises the real SimplArchiveApiClient ACL methods +
// the AclEntriesController list/set/revoke + grantable-principals endpoints.
[Collection(UiCollection.Name)]
public class DesktopManageAccessTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopManageAccessTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Grant_read_back_and_revoke_viewer_access()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.GetRepositoriesAsync())[0];
        var folderName = $"acl-{suffix}";
        await api.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.GetChildrenAsync(repo.Id)).First(c => c.Name == folderName);

        // A fresh active user to grant to — it shows up in the grantable-principals picker by display name.
        var granteeName = $"Grantee {suffix}";
        var granteeId = await api.CreateUserAsync($"grantee-{suffix}@simplarchive.local", granteeName);

        // A brand-new child inherits (no direct grants) and the admin can manage it (not forbidden).
        var initial = await api.GetAclAsync(folder.Id);
        Assert.False(initial.Forbidden);
        Assert.False(initial.BreaksInheritance);
        Assert.Empty(initial.Entries);
        Assert.Contains(initial.Principals, p => p.Type == "users" && p.Id == granteeId && p.Name == granteeName);

        // Grant the Viewer bundle (See + ReadContent).
        var viewer = new SimplArchiveApiClient.AclRights(
            CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false,
            CanCreateSubItems: false, CanDelete: false, CanMove: false, CanAnnotate: false, CanManagePermissions: false);
        await api.SetAclEntryAsync(folder.Id, "users", granteeId, viewer);

        // It reads back as a Viewer preset for that user.
        var afterGrant = await api.GetAclAsync(folder.Id);
        var entry = afterGrant.Entries.Single(e => e.PrincipalType == "users" && e.PrincipalId == granteeId);
        Assert.Equal("MaRoleViewer", ManageAccessViewModel.PresetLabelKey(entry.Rights));
        Assert.True(entry.Rights is { CanSee: true, CanReadContent: true, CanEditContent: false, CanManagePermissions: false });

        // Revoke → the grant is gone.
        await api.RevokeAclEntryAsync(folder.Id, "users", granteeId);
        Assert.DoesNotContain((await api.GetAclAsync(folder.Id)).Entries, e => e.PrincipalId == granteeId);

        await api.DeleteAsync(folder.Id); // clean up
    }
}
