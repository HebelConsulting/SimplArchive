namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Idempotently ensures the 3 well-known masks ("Basic Entry", "Folder", "eMail" — see
/// SimplArchive.Domain.Masks.WellKnownMaskIds) exist for a given tenant. Safe to call repeatedly — a
/// no-op if they already exist. Not wired to any tenant-creation trigger yet, since no such endpoint
/// exists (see ADR "Mask creation endpoint") — callable directly until real tenant onboarding (ADR
/// "Tenant provisioning / onboarding flow") exists to call it automatically.
/// </summary>
public interface IWellKnownMaskSeeder
{
    Task EnsureWellKnownMasksAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
