namespace SimplArchive.Application.Abstractions;

/// <summary>
/// When the current request's token is an impersonation token (ADR "User impersonation"), exposes the
/// impersonating (actor) admin's User id — read from the token's <c>impersonated_by</c> claim. Null for a
/// normal (non-impersonation) request. The <see cref="ICurrentUserAccessor"/> still resolves the *target*
/// user (the token's Subject); this names who is acting as them, for audit attribution + a UI banner.
/// </summary>
public interface ICurrentImpersonationAccessor
{
    Guid? ImpersonatorUserId { get; }
}
