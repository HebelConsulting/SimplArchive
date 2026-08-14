using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Inbox;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Inbox;

/// <summary>
/// Answers the one question every inbox action starts with: <i>which storage prefix is this caller allowed to
/// act in</i> — their own inbox, a group inbox they belong to, or another user's (ADR 0532).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>InboxController</c> when the page operations (issue #487) became a second controller that
/// needs exactly this. Copying it would have been the cheap move and the wrong one: an authorization rule with
/// two implementations is an authorization rule that will be tightened in one of them, and the copy that keeps
/// the old behaviour is a hole nothing points at. One implementation, two callers.
/// </para>
/// <para>
/// Returning <c>null</c> means "not allowed here" and the caller answers 403. Whether the item actually EXISTS
/// is deliberately a separate question the caller asks afterwards, so a probe cannot use the difference between
/// 403 and 404 to learn what sits in someone else's inbox.
/// </para>
/// </remarks>
public sealed class InboxScopeResolver(
    SimplArchiveDbContext dbContext,
    ICurrentTenantAccessor currentTenantAccessor,
    ICurrentUserAccessor currentUserAccessor,
    IUserSystemRightsResolver userSystemRights)
{
    /// <summary>
    /// Where an item lives, and who is acting. <see cref="UserId"/> is always the CALLER — it drives filing
    /// attribution and rights checks — while <see cref="Prefix"/> is the inbox being acted on, which for a group
    /// or an administered user's inbox is somebody else's.
    /// </summary>
    public sealed record InboxScope(Guid TenantId, Guid UserId, string Prefix);

    public (Guid TenantId, Guid UserId)? Caller() =>
        currentTenantAccessor.TenantId is { } tenantId && currentUserAccessor.UserId is { } userId
            ? (tenantId, userId)
            : null;

    public static string UserPrefix(Guid tenantId, Guid userId) => InboxScopePrefix.ForUser(tenantId, userId);

    // A group inbox is the exact peer of the per-user inbox, keyed by group (ADR 0532) — implicit for every
    // group, access = effective group membership. The formula itself lives in Infrastructure, because the
    // Worker's sweep needs it too and cannot reference the Api (ADR 0576).
    public static string GroupPrefix(Guid tenantId, Guid groupId) => InboxScopePrefix.ForGroup(tenantId, groupId);

    /// <summary>
    /// Resolves + authorizes the storage scope of an inbox item addressed by name plus an optional source
    /// selector: own inbox (neither set), a group inbox the caller is an effective member of, or a specific
    /// user's inbox — the caller's own, or any user's for a <c>CanManageInboxes</c> holder. A mask sidecar is
    /// never addressable as an item.
    /// </summary>
    public async Task<InboxScope?> ResolveAsync(
        Guid? group,
        Guid? user,
        string name,
        CancellationToken cancellationToken)
    {
        if (Caller() is not var (tenantId, callerId) || IsMaskSidecar(name))
        {
            return null;
        }

        if (group is { } groupId)
        {
            var groups = await GroupMembershipExpansion.GetEffectiveGroupIdsForUserAsync(dbContext, callerId, cancellationToken);
            return groups.Contains(groupId) ? new InboxScope(tenantId, callerId, GroupPrefix(tenantId, groupId)) : null;
        }

        if (user is { } userId && userId != callerId)
        {
            // Another user's inbox — admin-gated (CanManageInboxes).
            return await CanManageInboxesAsync(callerId, cancellationToken)
                ? new InboxScope(tenantId, callerId, UserPrefix(tenantId, userId))
                : null;
        }

        // Neither source (or ?user= is the caller themselves) → the caller's own inbox.
        return new InboxScope(tenantId, callerId, UserPrefix(tenantId, callerId));
    }

    public async Task<bool> CanManageInboxesAsync(Guid userId, CancellationToken cancellationToken) =>
        (await userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageInboxes;

    public static bool IsMaskSidecar(string name) =>
        name.EndsWith(InboxPageService.MaskSidecarSuffix, StringComparison.OrdinalIgnoreCase);
}
