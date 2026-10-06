namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The audit action codes the facade's floor records for a module's writes (ABI 1.9, ADR 0900). They are the CORE's
/// own document vocabulary (<c>AuditActions</c> in the Api, which Infrastructure cannot reference), so a module's
/// act reads like anyone else's in the trail; the details say "by module X". <c>ModuleWriteAuditTests</c> holds each
/// equal to its Api twin.
/// </summary>
public static class ModuleWriteAudit
{
    public const string Created = "Document.Created";
    public const string IndexDataUpdated = "Document.IndexDataUpdated";
    public const string Renamed = "Document.Renamed";
    public const string VersionAdded = "Document.VersionAdded";
    public const string ReferenceAdded = "Reference.Added";
}
