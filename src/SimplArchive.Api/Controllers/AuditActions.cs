namespace SimplArchive.Api.Controllers;

/// <summary>
/// Stable audit action codes (ADR "Audit trail (first slice)") — the <c>AuditEvent.Action</c> values. Kept
/// as constants so recording sites and any future filtering share one vocabulary.
/// </summary>
public static class AuditActions
{
    public const string AclGranted = "Acl.Granted";
    public const string AclRevoked = "Acl.Revoked";
    public const string AclInheritanceBroken = "Acl.InheritanceBroken";
    public const string AclInheritanceRestored = "Acl.InheritanceRestored";

    public const string DocumentDeleted = "Document.Deleted";
    public const string DocumentRestored = "Document.Restored";
    public const string DocumentMoved = "Document.Moved";

    // Document content/metadata lifecycle (ADR "Audit every-mutation coverage — document lifecycle").
    public const string DocumentCreated = "Document.Created";
    public const string RepositoryCreated = "Repository.Created";
    public const string DocumentRenamed = "Document.Renamed";
    public const string DocumentVersionAdded = "Document.VersionAdded";

    public const string DocumentVersionRestored = "Document.VersionRestored";
    public const string DocumentMaskAssigned = "Document.MaskAssigned";
    public const string DocumentMaskCleared = "Document.MaskCleared";
    public const string DocumentIndexDataUpdated = "Document.IndexDataUpdated";

    // External-system correlation key set/cleared on a document (ADR 0520).
    public const string DocumentOriginSet = "Document.OriginSet";
    public const string DocumentOriginCleared = "Document.OriginCleared";

    public const string DocumentTagsUpdated = "Document.TagsUpdated";
    public const string DocumentDateChanged = "Document.DocumentDateChanged";
    public const string DocumentContentsSortOrderChanged = "Document.ContentsSortOrderChanged";
    public const string DocumentOcrLanguagesChanged = "Document.OcrLanguagesChanged";
    // The data-classification / sensitivity label changed (ADR "Data classification / sensitivity labels").
    public const string DocumentSensitivityChanged = "Document.SensitivityChanged";

    // Collaboration events (ADR "Audit collaboration events" — the second half of every-mutation coverage).
    public const string CommentPosted = "Comment.Posted";
    public const string ReferenceAdded = "Reference.Added";
    public const string ReferenceRemoved = "Reference.Removed";
    public const string AnnotationAdded = "Annotation.Added";
    public const string AnnotationEdited = "Annotation.Edited";
    public const string AnnotationRemoved = "Annotation.Removed";
    // Permanent removal of a recycle-bin document (ADR "Manual hard-delete / purge").
    public const string DocumentPurged = "Document.Purged";
    // Filed from the inbox — as a new document in a folder or as a new version of an existing one (ADR "Audit
    // tenant-settings, inbox filing + personal-repository creation").
    public const string DocumentFiled = "Document.Filed";
    // Import of an archive's root (ADR "Repository import").
    public const string DocumentImported = "Document.Imported";
    // Auto-disposed by the retention sweep (ADR "Retention policies (auto-disposition)"). Also used as a literal
    // in Infrastructure's RetentionService, which can't reference this Api-layer class.
    public const string DocumentRetentionDisposed = "Document.RetentionDisposed";

    // A records manager extended a document's retention during disposition review (ADR "Retention
    // review-before-disposition").
    public const string DocumentRetentionExtended = "Document.RetentionExtended";
    // Check-out (exclusive edit lock) — ADR "Document check-out / check-in".
    public const string DocumentCheckedOut = "Document.CheckedOut";
    public const string DocumentCheckedIn = "Document.CheckedIn";
    // The holder (or a CanOverrideCheckout admin) reset a check-out's idle timer (ADR "Self-service check-out
    // extension").
    public const string DocumentCheckoutExtended = "Document.CheckoutExtended";
    // A CanOverrideCheckout holder force-released someone else's lock.
    public const string DocumentCheckoutOverridden = "Document.CheckoutOverridden";
    // The background sweep auto-released a check-out idle past the tenant's TTL (ADR "Stale check-out
    // auto-release sweep"). Actor is System; mirrored as a literal in StaleCheckoutService.
    public const string DocumentCheckoutExpired = "Document.CheckoutExpired";

    public const string LegalHoldPlaced = "LegalHold.Placed";
    public const string LegalHoldReleased = "LegalHold.Released";
    public const string LegalHoldItemAdded = "LegalHold.ItemAdded";
    public const string LegalHoldItemRemoved = "LegalHold.ItemRemoved";

    public const string WorkflowSubmitted = "Workflow.Submitted";
    public const string WorkflowApproved = "Workflow.Approved";
    public const string WorkflowRejected = "Workflow.Rejected";
    public const string WorkflowReleased = "Workflow.Released";
    public const string WorkflowReassigned = "Workflow.Reassigned";

    public const string UserCreated = "User.Created";
    public const string UserDeactivated = "User.Deactivated";
    public const string UserReactivated = "User.Reactivated";
    public const string UserRightsChanged = "User.RightsChanged";
    public const string UserPasswordChanged = "User.PasswordChanged";
    public const string UserPasswordReset = "User.PasswordReset";
    public const string UserMfaEnabled = "User.MfaEnabled";
    public const string UserMfaDisabled = "User.MfaDisabled";
    public const string UserMfaReset = "User.MfaReset";
    public const string PasskeyRegistered = "User.PasskeyRegistered";
    public const string PasskeyRemoved = "User.PasskeyRemoved";

    public const string GroupCreated = "Group.Created";
    public const string GroupDeleted = "Group.Deleted";
    public const string GroupRightsChanged = "Group.RightsChanged";
    public const string GroupMemberAdded = "Group.MemberAdded";
    public const string GroupMemberRemoved = "Group.MemberRemoved";

    public const string ServiceAccountCreated = "ServiceAccount.Created";
    public const string ServiceAccountSecretRotated = "ServiceAccount.SecretRotated";
    public const string ServiceAccountRevoked = "ServiceAccount.Revoked";

    public const string TenantCreated = "Tenant.Created";
    // A tenant admin changed the tenant's self-service settings (ADR "Audit tenant-settings, inbox filing +
    // personal-repository creation") — the details carry the field-level before→after changes (secret redacted).
    public const string TenantSettingsUpdated = "Tenant.SettingsUpdated";

    // A tenant admin listed the tenant's users' personal spaces (ADR "Tenant-admin Administration → Users view")
    // — admin access to private spaces is recorded, not silent.
    public const string AdminViewedPersonalSpaces = "Admin.ViewedPersonalSpaces";

    public const string LoggedIn = "Auth.LoggedIn";
    // Impersonation token issued (ADR "User impersonation") — actor = the impersonating admin, target = the user.
    public const string ImpersonationStarted = "Auth.ImpersonationStarted";
}
