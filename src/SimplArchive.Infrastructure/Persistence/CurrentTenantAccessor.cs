using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Scoped, settable ICurrentTenantAccessor implementation. Nothing sets TenantId yet — once auth exists,
/// Api middleware sets it from the JWT tenant claim per request, and Worker's job-processing loop sets it
/// from the job payload per item, so both hosts share this one implementation rather than needing separate
/// per-host accessors.
/// </summary>
public class CurrentTenantAccessor : ICurrentTenantAccessor
{
    public Guid? TenantId { get; set; }
}
