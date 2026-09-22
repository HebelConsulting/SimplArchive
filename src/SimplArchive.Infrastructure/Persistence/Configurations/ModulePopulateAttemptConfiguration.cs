using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ModulePopulateAttemptConfiguration : IEntityTypeConfiguration<ModulePopulateAttempt>
{
    public void Configure(EntityTypeBuilder<ModulePopulateAttempt> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.MachineId)
            .IsRequired()
            .HasMaxLength(128);   // as ModuleContentHealth.MachineId

        // One clock per (machine, subject) — the write is an UPSERT, never an accumulating attempt log.
        builder.HasIndex(a => new { a.TenantId, a.MachineId, a.SubjectDocumentId }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // CASCADE with the folder, unlike ModuleContentHealth's deliberate no-FK: a health row is a record
        // ABOUT a folder that an administrator still wants after its deletion; this row is a rate-limit
        // clock with no meaning beyond the folder's lifetime (owner-approved shape, 2026-09-22, #1307).
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(a => a.SubjectDocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
