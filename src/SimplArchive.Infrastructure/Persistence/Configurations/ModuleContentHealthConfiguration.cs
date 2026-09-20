using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ModuleContentHealthConfiguration : IEntityTypeConfiguration<ModuleContentHealth>
{
    public void Configure(EntityTypeBuilder<ModuleContentHealth> builder)
    {
        builder.HasKey(h => h.Id);

        builder.Property(h => h.ModuleId)
            .IsRequired()
            .HasMaxLength(128);   // as ModuleActivation.ModuleId

        builder.Property(h => h.MachineId)
            .IsRequired()
            .HasMaxLength(128);

        // Capped for the reason the audit-webhook's LastError is: this is rendered into an administrator's
        // status line, and a provider that answers with an essay would otherwise own that whole screen.
        builder.Property(h => h.LastError)
            .IsRequired()
            .HasMaxLength(500);

        // One row per source. This is what makes the write an UPSERT rather than an accumulating log of
        // every failure — and it is also what makes the threshold crossing detectable under a race: two
        // instances cannot both insert, so one of them updates a row the other created.
        builder.HasIndex(h => new { h.TenantId, h.ModuleId, h.MachineId, h.SubjectDocumentId }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(h => h.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deliberately NO foreign key to Document for SubjectDocumentId — see the entity's remarks. A row is
        // a record ABOUT a folder, not a part of it, and it must not join the document's delete graph.
        //
        // Nor to ModuleActivation: a source can be failing when a licence lapses, and deleting the record of
        // WHY as a side effect of deactivation is exactly the shape ModuleSettingValue avoids.
    }
}
