namespace SimplArchive.Infrastructure.Conversion;

// A durable "generate a searchable-PDF successor version from this TIFF version" event (ADR "Searchable PDF
// successor for TIFFs"). DocumentFinalizer enqueues one after any TIFF version is confirmed;
// SearchablePdfWorker drains it in the background, off the request path. Like SearchIndexOutbox: NOT
// ITenantScoped (the worker spans all tenants and sets the tenant context per row) and NOT FK'd (a row must
// survive independently). SourceVersionId identifies exactly which TIFF version's bytes to convert, so the
// workflow applies to any TIFF version, not only v1.
public sealed class SearchablePdfOutbox
{
    public Guid Id { get; set; }

    // The tenant of the document/version, set into the tenant context before the (tenant-filtered) reads run.
    public Guid TenantId { get; set; }

    public Guid DocumentId { get; set; }

    // The confirmed TIFF version whose bytes get OCR'd into the searchable-PDF successor.
    public Guid SourceVersionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Retry/backoff bookkeeping — the worker gives up (drops the row) after a cap so a permanently-bad file
    // doesn't retry forever.
    public int Attempts { get; set; }
}
