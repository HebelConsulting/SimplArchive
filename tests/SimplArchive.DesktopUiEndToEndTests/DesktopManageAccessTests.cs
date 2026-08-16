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

        var repo = (await api.Documents.GetRepositoriesAsync())[0];
        var folderName = $"acl-{suffix}";
        await api.Documents.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == folderName);

        // A fresh active user to grant to — it shows up in the grantable-principals picker by display name.
        var granteeName = $"Grantee {suffix}";
        var granteeId = await api.Admin.CreateUserAsync($"grantee-{suffix}@simplarchive.local", granteeName);

        // A brand-new child inherits (no direct grants) and the admin can manage it (not forbidden).
        var initial = await api.Documents.GetAclAsync(folder.Id);
        Assert.False(initial.Forbidden);
        Assert.False(initial.BreaksInheritance);
        Assert.Empty(initial.Entries);
        Assert.Contains(initial.Principals, p => p.Type == "users" && p.Id == granteeId.Id && p.Name == granteeName);

        // Grant the Viewer bundle (See + ReadContent).
        var viewer = new AclRights(
            CanSee: true, CanReadContent: true, CanEditContent: false, CanEditIndexData: false,
            CanCreateSubItems: false, CanDelete: false, CanMove: false, CanAnnotate: false, CanManagePermissions: false);
        var grantable = (await api.Documents.GetAclAsync(folder.Id)).Principals.Single(p => p.Type == "users" && p.Id == granteeId.Id);
        await api.SetAclEntryAsync(grantable, viewer);

        // It reads back as a Viewer preset for that user.
        var afterGrant = await api.Documents.GetAclAsync(folder.Id);
        var entry = afterGrant.Entries.Single(e => e.PrincipalType == "users" && e.PrincipalId == granteeId.Id);
        Assert.Equal("MaRoleViewer", ManageAccessViewModel.PresetLabelKey(entry.Rights));
        Assert.True(entry.Rights is { CanSee: true, CanReadContent: true, CanEditContent: false, CanManagePermissions: false });

        // Revoke → the grant is gone.
        await api.Documents.RevokeAclEntryAsync(entry);
        Assert.DoesNotContain((await api.Documents.GetAclAsync(folder.Id)).Entries, e => e.PrincipalId == granteeId.Id);

        await api.Documents.DeleteAsync(folder.Id); // clean up
    }

    [Fact]
    public async Task Break_copies_inherited_grants_down_and_restore_clears_them()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.Documents.GetRepositoriesAsync())[0];
        var folderName = $"inh-{suffix}";
        await api.Documents.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == folderName);

        // A fresh child inherits — no own grants.
        var before = await api.Documents.GetAclAsync(folder.Id);
        Assert.False(before.BreaksInheritance);
        Assert.Empty(before.Entries);

        // Break → the governing (root) grants are copied down, so the folder now has its own grants. The href
        // comes from the resource, as the view model does it (#426) — a child folder advertises the rel.
        Assert.NotNull(before.InheritanceHref);
        await api.Documents.SetInheritanceAsync(before.InheritanceHref!, true);
        var broken = await api.Documents.GetAclAsync(folder.Id);
        Assert.True(broken.BreaksInheritance);
        Assert.NotEmpty(broken.Entries);

        // Restore → own grants discarded, inherits again.
        await api.Documents.SetInheritanceAsync(broken.InheritanceHref!, false);
        var restored = await api.Documents.GetAclAsync(folder.Id);
        Assert.False(restored.BreaksInheritance);
        Assert.Empty(restored.Entries);

        // A repository root has no parent to inherit from, so the server does not advertise the rel at all and
        // neither client draws the toggle (#426). The refusal behind it still stands, but a conforming client
        // never reaches it — which is the point: the affordance is absent rather than certain to fail.
        var rootAcl = await api.Documents.GetAclAsync(repo.Id);
        Assert.Null(rootAcl.InheritanceHref);

        await api.Documents.DeleteAsync(folder.Id); // clean up
    }

    [Fact]
    public async Task Effective_access_resolves_groups_to_members_and_flags_admins()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var repo = (await api.Documents.GetRepositoriesAsync())[0];
        var folderName = $"eff-{suffix}";
        await api.Documents.CreateFolderAsync(repo.Id, folderName);
        var folder = (await api.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.Name == folderName);

        // Break inheritance so the folder's own grants actually govern it — a grant on a still-inheriting item is
        // a no-op (only the governing scope's grants apply, ADR 0183).
        await api.Documents.SetInheritanceAsync((await api.Documents.GetAclAsync(folder.Id)).InheritanceHref!, true);

        // A group with a member.
        var groupName = $"grp-{suffix}";
        var groupId = await api.Admin.CreateGroupAsync(groupName);
        var userId = await api.Admin.CreateUserAsync($"member-{suffix}@simplarchive.local", $"Member {suffix}");
        await api.Admin.AddGroupMemberAsync(groupId, userId.Id);

        // Grant the group Viewer directly on the folder (now the governing scope).
        var viewer = new AclRights(true, true, false, false, false, false, false, false, false);
        var grantableGroup = (await api.Documents.GetAclAsync(folder.Id)).Principals.Single(p => p.Type == "groups" && p.Id == groupId.Id);
        await api.SetAclEntryAsync(grantableGroup, viewer);

        var eff = await api.Documents.GetEffectiveAccessAsync(folder.Id);

        // The group appears as a direct grant, and its member is resolved as accessing "via group".
        Assert.Contains(eff.Entries, e => e.Type == "groups" && e.Id == groupId.Id && e.Access == "direct");
        Assert.Contains(eff.Entries, e => e.Type == "users" && e.Id == userId.Id && e.Access == "group" && e.ViaGroup == groupName);
        // The demo admin bypasses the ACL — flagged as a tenant admin.
        Assert.Contains(eff.Entries, e => e.Type == "users" && e.Access == "admin");

        await api.Documents.DeleteAsync(folder.Id); // clean up
    }
}
