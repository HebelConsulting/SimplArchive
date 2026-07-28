namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Resolves the current logged-in User for the in-flight request, read from the JWT's Subject claim when
/// the token carries the User marker claim (see ADR "Interactive User login (foundation slice)"). Mirrors
/// ICurrentServiceAccountAccessor exactly — mutually exclusive with it and with
/// ICurrentPlatformAdministratorAccessor per request, since a token belongs to exactly one principal type.
/// </summary>
public interface ICurrentUserAccessor
{
    Guid? UserId { get; }
}
