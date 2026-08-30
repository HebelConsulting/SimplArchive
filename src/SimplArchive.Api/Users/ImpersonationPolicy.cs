using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Users;

namespace SimplArchive.Api.Users;

/// <summary>
/// Who may be impersonated, written once (#875).
/// </summary>
/// <remarks>
/// <para>
/// The clients used to state this rule themselves — "not an admin, not an impersonator" read off the target
/// row — and the web copy was **wrong**: the listing sends each user's OWN columns
/// (<c>SystemRightsMapping.Read</c>), while the token endpoint refuses on EFFECTIVE rights
/// (own ∪ groups). So a user who is a tenant admin *via a group* was offered as a target and refused on click.
/// </para>
/// <para>
/// Under ADR 0722 a conditionally-emitted rel and the endpoint it points at must resolve their condition
/// through the same code, not through two computations that happen to agree — and here they did not even agree.
/// Both <c>TokenController</c> and the principal listing now call this.
/// </para>
/// </remarks>
public static class ImpersonationPolicy
{
    /// <summary>Whether <paramref name="actorId"/> may impersonate <paramref name="target"/>.</summary>
    /// <param name="actorRights">The actor's EFFECTIVE rights (own ∪ groups).</param>
    /// <param name="actorIsAlreadyImpersonating">Impersonation does not nest.</param>
    /// <param name="targetRights">The target's EFFECTIVE rights — the half the clients got wrong.</param>
    public static bool MayImpersonate(
        Guid actorId,
        SystemRightsSet actorRights,
        bool actorIsAlreadyImpersonating,
        User target,
        SystemRightsSet targetRights) =>
        !actorIsAlreadyImpersonating
        && actorRights.CanImpersonate
        && target.IsActive
        && target.Id != actorId
        && !targetRights.IsTenantAdmin
        && !targetRights.CanImpersonate;
}
