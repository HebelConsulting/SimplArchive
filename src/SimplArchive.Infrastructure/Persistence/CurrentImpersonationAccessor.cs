using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Scoped, settable <see cref="ICurrentImpersonationAccessor"/> implementation — set by Api middleware from the
/// token's <c>impersonated_by</c> claim when present (ADR "User impersonation").
/// </summary>
public class CurrentImpersonationAccessor : ICurrentImpersonationAccessor
{
    public Guid? ImpersonatorUserId { get; set; }
}
