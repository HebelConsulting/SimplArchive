namespace SimplArchive.Application.Abstractions;

// Idempotently ensures a tenant has the default sensitivity labels (ADR "Configurable sensitivity labels +
// upload defaults") — Public / Internal / Confidential / Restricted (None is the absence of a label). Called at
// tenant provisioning; safe to call repeatedly (a no-op if they already exist by name). Mirrors
// IWellKnownMaskSeeder.
public interface ISensitivityLabelSeeder
{
    Task EnsureDefaultLabelsAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
