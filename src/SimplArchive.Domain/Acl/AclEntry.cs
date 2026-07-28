using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Acl;

// Grants a set of rights to a principal (User, Group, or ServiceAccount — exactly one of UserId/GroupId/
// ServiceAccountId is set) on a Document — see ADR "ACL entry data shape (repository-scoped slice)", ADR
// "AclEntry ServiceAccount principal (schema-only slice)", ADR "Document ACL inheritance data shape
// (schema-only slice)". Document-scoped only since ADR "Repository/Document unification" — a "repository-
// level" grant is now just a grant on a root Document (ParentId == null); DocumentId is always required,
// no more Repository/Document XOR. Non-root-document rows only make sense when that Document's
// BreaksInheritance is true — see Document.BreaksInheritance.
public class AclEntry : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    public Guid? UserId { get; set; }

    public Guid? GroupId { get; set; }

    public Guid? ServiceAccountId { get; set; }

    public bool CanSee { get; set; }

    public bool CanReadContent { get; set; }

    public bool CanEditContent { get; set; }

    public bool CanEditIndexData { get; set; }

    public bool CanDelete { get; set; }

    public bool CanCreateSubItems { get; set; }

    // Lets the grantee move (reparent) this item into another folder — see ADR "Desktop drag-and-drop
    // move and reference". A distinct right from CanDelete: moving is re-filing, not removal.
    public bool CanMove { get; set; }

    // Lets the grantee manage this repository's ACL entries themselves — see ADR "ACL management
    // right". Capped by ADR's escalation limit: a holder can only grant rights that are a subset of
    // their own current effective rights (see EffectiveRights.Covers).
    public bool CanManagePermissions { get; set; }

    // Lets the grantee create + edit their own sticky-note annotations on this item — see ADR "CanAnnotate
    // right". Reading notes needs only CanReadContent; deleting is the author or a CanEditContent holder.
    public bool CanAnnotate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
