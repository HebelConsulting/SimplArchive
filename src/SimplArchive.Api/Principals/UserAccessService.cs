using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Principals;

/// <summary>
/// The caller-access questions about PRINCIPALS that more than one controller has to ask.
/// </summary>
/// <remarks>
/// <para>
/// Extracted when the password endpoints moved to their own controller (#849): the alternative was a second
/// copy of a nineteen-line rights walk, and a copy is how the fourth caller gets the fix the first three do
/// not. Same shape and same reason as <c>DocumentAccessService</c>, which eight controllers were copying
/// before ADR 0571.
/// </para>
/// <para>
/// A named class in the Api project rather than a handler layer or a method on the entity — business logic
/// lives here (ADR 0214).
/// </para>
/// </remarks>
public sealed class UserAccessService(
    SimplArchiveDbContext dbContext,
    ICurrentServiceAccountAccessor serviceAccounts,
    ICurrentUserAccessor users,
    IUserSystemRightsResolver systemRights)
{
    /// <summary>Whether the caller may manage users — a service account's own flag, or a user's EFFECTIVE right.</summary>
    /// <remarks>
    /// Effective rather than direct, so <c>CanManageUsers</c> held through a group takes effect (ADR 0309).
    /// Resolving only the direct right is the version that looks correct and quietly denies every
    /// administrator whose rights come from the group they are in.
    /// </remarks>
    public async Task<bool> CanManageUsersAsync(CancellationToken cancellationToken)
    {
        if (serviceAccounts.ServiceAccountId is { } serviceAccountId)
        {
            return await dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanManageUsers)
                .SingleAsync(cancellationToken);
        }

        if (users.UserId is { } userId)
        {
            return (await systemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageUsers;
        }

        return false;
    }
}
