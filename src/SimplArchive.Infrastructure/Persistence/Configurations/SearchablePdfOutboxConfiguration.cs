using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Infrastructure.Conversion;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class SearchablePdfOutboxConfiguration : IEntityTypeConfiguration<SearchablePdfOutbox>
{
    public void Configure(EntityTypeBuilder<SearchablePdfOutbox> builder)
    {
        builder.HasKey(o => o.Id);

        // The worker drains oldest-first; (CreatedAt, Id) is the poll order and tiebreaker.
        builder.HasIndex(o => new { o.CreatedAt, o.Id });

        // No FK on DocumentId/SourceVersionId and no tenant FK / ITenantScoped filter (the worker reads across
        // every tenant and sets the tenant context per row) — see SearchablePdfOutbox.
    }
}
