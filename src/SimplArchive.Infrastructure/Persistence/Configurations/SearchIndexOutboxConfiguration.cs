using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Infrastructure.Search;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class SearchIndexOutboxConfiguration : IEntityTypeConfiguration<SearchIndexOutbox>
{
    public void Configure(EntityTypeBuilder<SearchIndexOutbox> builder)
    {
        builder.HasKey(o => o.Id);

        // The worker drains oldest-first; (EnqueuedAt, Id) is the poll order and tiebreaker.
        builder.HasIndex(o => new { o.EnqueuedAt, o.Id });

        // No FK on DocumentId (a deleted document's row must survive to process the removal) and no tenant
        // FK / ITenantScoped filter (the worker reads across every tenant) — see SearchIndexOutbox.
    }
}
