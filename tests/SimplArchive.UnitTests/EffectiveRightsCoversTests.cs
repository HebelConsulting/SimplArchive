using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;

namespace SimplArchive.UnitTests;

public class EffectiveRightsCoversTests
{
    [Fact]
    public void Allows_granting_a_right_the_granter_holds()
    {
        var granter = new EffectiveRights(
            CanSee: true, CanReadContent: true, CanEditContent: false,
            CanEditIndexData: false, CanDelete: false, CanCreateSubItems: false, CanManagePermissions: true, CanMove: true, CanAnnotate: true);

        var proposed = new AclEntry { CanSee = true, CanReadContent = true };

        Assert.True(granter.Covers(proposed));
    }

    [Fact]
    public void Rejects_granting_a_right_the_granter_does_not_hold()
    {
        var granter = new EffectiveRights(
            CanSee: true, CanReadContent: true, CanEditContent: false,
            CanEditIndexData: false, CanDelete: false, CanCreateSubItems: false, CanManagePermissions: true, CanMove: true, CanAnnotate: true);

        // Granter lacks CanDelete but tries to grant it to someone else.
        var proposed = new AclEntry { CanSee = true, CanDelete = true };

        Assert.False(granter.Covers(proposed));
    }

    [Fact]
    public void Rejects_granting_Move_if_the_granter_lacks_it()
    {
        var granter = new EffectiveRights(
            CanSee: true, CanReadContent: true, CanEditContent: true,
            CanEditIndexData: true, CanDelete: true, CanCreateSubItems: true, CanManagePermissions: true, CanMove: false, CanAnnotate: false);

        var proposed = new AclEntry { CanSee = true, CanMove = true };

        Assert.False(granter.Covers(proposed));
    }

    [Fact]
    public void Rejects_granting_Annotate_if_the_granter_lacks_it()
    {
        var granter = new EffectiveRights(
            CanSee: true, CanReadContent: true, CanEditContent: true,
            CanEditIndexData: true, CanDelete: true, CanCreateSubItems: true, CanManagePermissions: true, CanMove: true, CanAnnotate: false);

        var proposed = new AclEntry { CanSee = true, CanAnnotate = true };

        Assert.False(granter.Covers(proposed));
    }

    [Fact]
    public void Rejects_granting_ManagePermissions_itself_if_the_granter_lacks_it()
    {
        var granter = new EffectiveRights(
            CanSee: true, CanReadContent: true, CanEditContent: true,
            CanEditIndexData: true, CanDelete: true, CanCreateSubItems: true, CanManagePermissions: false, CanMove: false, CanAnnotate: false);

        var proposed = new AclEntry { CanManagePermissions = true };

        Assert.False(granter.Covers(proposed));
    }
}
