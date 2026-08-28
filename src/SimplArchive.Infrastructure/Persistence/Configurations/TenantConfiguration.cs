using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);

        // Default OCR languages (ADR "Per-tenant / per-version OCR languages"). NOT NULL with a store default
        // so the migration backfills existing tenants; new tenants get it from the entity initializer.
        builder.Property(t => t.DefaultOcrLanguages)
            .IsRequired()
            .HasMaxLength(200)
            .HasDefaultValue(SimplArchive.Domain.Documents.OcrLanguages.Default);

        // Audit-log retention + the tamper-evidence chain's retained-window checkpoint (ADR "Audit trail
        // retention and purge"). Store defaults so the migration backfills existing tenants; new tenants get
        // them from the entity initializer.
        builder.Property(t => t.AuditRetentionDays).HasDefaultValue(365);

        // Stale-check-out auto-release threshold (ADR "Stale check-out auto-release sweep"). Store default 0
        // (disabled) so the migration backfills existing tenants; new tenants get it from the entity default.
        builder.Property(t => t.CheckoutTtlDays).HasDefaultValue(0);
        builder.Property(t => t.CheckoutWarningDays).HasDefaultValue(1);

        // HasDefaultValue, not just the C# initializer (#793): the initializer applies only to NEW rows built
        // in this process; the store default is what EXISTING tenants receive when the column is added. Without
        // it the migration writes DEFAULT false and every pre-upgrade tenant seeds its new users mail-only —
        // the opposite of the decision.
        builder.Property(t => t.ImapShowAllDocumentsDefault).HasDefaultValue(true);

        // External links (ADR 0546). HasDefaultValue, not just the C# initializer: the initializer only applies to
        // objects created in code, so without these an EXISTING tenant would be migrated to 0 — which would mean
        // "links may expire at most 0 days out" and silently make the feature unusable for every tenant that
        // predates it. AllowExternalLinks stays false by design, so the caps only matter once someone opts in —
        // the DEMO tenant is the one exception, switched on by DemoDataSeeder so the kiosk can show the feature.
        builder.Property(t => t.ExternalLinkMaxDays).HasDefaultValue(180);
        builder.Property(t => t.ExternalLinkDefaultAccesses).HasDefaultValue(5);

        // WORM Object Lock retention mode (ADR "WORM / immutable document versions"). Store default Governance
        // (0) so the migration backfills existing tenants; new tenants get it from the entity default.
        builder.Property(t => t.WormLockMode).HasDefaultValue(SimplArchive.Domain.Tenants.WormLockMode.Governance);

        // Tenant-wide require-MFA policy (ADR "MFA require-policy + TOTP secret encryption"). Store default
        // false so the migration backfills existing tenants; new tenants get it from the entity default.
        builder.Property(t => t.RequireMfa).HasDefaultValue(false);
        // Passwordless passkey sign-in is allowed by default (ADR "Passwordless passkey login on by default"). Store
        // default true so the migration backfills existing tenants to true; new tenants get it from the entity default.
        builder.Property(t => t.AllowPasskeyLogin).HasDefaultValue(true);
        builder.Property(t => t.AuditChainStartSequence).HasDefaultValue(0L);
        // Audit-log WORM archive checkpoint (ADR "Audit-log WORM"). Store default -1 (nothing archived) so the
        // migration backfills existing tenants; new tenants get it from the entity default.
        builder.Property(t => t.AuditWormArchivedThrough).HasDefaultValue(-1L);
        builder.Property(t => t.AuditWebhookDeliveredThrough).HasDefaultValue(-1L);
        builder.Property(t => t.AuditWebhookLastError).HasMaxLength(500);
        builder.Property(t => t.AuditChainStartPreviousHash)
            .IsRequired()
            .HasMaxLength(64)
            .HasDefaultValue(SimplArchive.Domain.Audit.AuditChain.GenesisHash);

        // Unique among Active tenants only — a deactivated tenant (mid-grace-period, see ADR "Tenant
        // offboarding / deletion flow") doesn't reserve its name (see ADR "Tenant name uniqueness").
        // TenantStatus.Active's underlying value is 0 — same technique as Repository.Name (ADR
        // "Repository name uniqueness").
        builder.HasIndex(t => t.Name)
            .IsUnique()
            .HasFilter("\"Status\" = 0");

        // Defense-in-depth backstop keeping Status and DeactivatedAt from drifting out of sync — see ADR
        // "Status/DeactivatedAt consistency for Tenant and Repository".
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Tenants_Status_DeactivatedAt",
            "(\"Status\" = 0 AND \"DeactivatedAt\" IS NULL) OR (\"Status\" = 1 AND \"DeactivatedAt\" IS NOT NULL)"));
    }
}
