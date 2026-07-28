namespace SimplArchive.Domain.Acl;

// Granular per-document rights — see ADR "Permissions / access control model". Application-level
// convenience for expressing a right in code; AclEntry itself stores these as individual boolean
// columns, not this enum (see ADR "ACL entry data shape (repository-scoped slice)").
public enum Right
{
    See,
    ReadContent,
    EditContent,
    EditIndexData,
    Delete,
    CreateSubItems,

    // See ADR "Desktop drag-and-drop move and reference" — lets the grantee move (reparent) the item into
    // another folder. Distinct from Delete: moving re-files, it doesn't remove.
    Move,

    // See ADR "ACL management right" — lets the grantee manage this repository's own ACL entries,
    // capped so they can only grant rights they themselves currently hold (EffectiveRights.Covers).
    ManagePermissions,

    // See ADR "CanAnnotate right" — lets the grantee create + edit their own sticky-note annotations.
    Annotate,
}
