using SimplArchive.Domain.Acl;

namespace SimplArchive.Application.Abstractions;

// The union of rights a user actually has on a repository, combining direct grants and grants via
// (possibly nested) group membership — see ADR "Effective rights computation".
public record EffectiveRights(
    bool CanSee,
    bool CanReadContent,
    bool CanEditContent,
    bool CanEditIndexData,
    bool CanDelete,
    bool CanCreateSubItems,
    bool CanManagePermissions,
    bool CanMove,
    bool CanAnnotate)
{
    // See ADR "ACL management right": a CanManagePermissions holder can only grant rights that are a
    // subset of their own effective rights. Returns false if `proposed` grants anything this instance
    // doesn't already have.
    public bool Covers(AclEntry proposed)
    {
        return (!proposed.CanSee || CanSee)
            && (!proposed.CanReadContent || CanReadContent)
            && (!proposed.CanEditContent || CanEditContent)
            && (!proposed.CanEditIndexData || CanEditIndexData)
            && (!proposed.CanDelete || CanDelete)
            && (!proposed.CanCreateSubItems || CanCreateSubItems)
            && (!proposed.CanManagePermissions || CanManagePermissions)
            && (!proposed.CanMove || CanMove)
            && (!proposed.CanAnnotate || CanAnnotate);
    }
}
