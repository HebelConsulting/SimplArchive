using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Scoped, settable ICurrentUserAccessor implementation — mirrors CurrentServiceAccountAccessor exactly.
/// Set by Api middleware from the JWT's Subject claim per request when the token carries the User marker
/// claim (see ADR "Interactive User login (foundation slice)").
/// </summary>
public class CurrentUserAccessor : ICurrentUserAccessor
{
    public Guid? UserId { get; set; }
}
