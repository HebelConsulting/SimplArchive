namespace SimplArchive.Domain.Masks;

// Fixed, cross-tenant Mask.Id values — every tenant's "Basic Entry"/"Folder"/"eMail" mask shares the exact
// same Id, matching how a real DMS ships a fixed set of default document-type identifiers rather than
// each tenant getting its own randomly-generated ones. See ADR "Mask composite primary key for cross-tenant
// well-known IDs" and ADR "Mask creation endpoint".
public static class WellKnownMaskIds
{
    public static readonly Guid BasicEntry = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E30");

    public static readonly Guid Folder = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E31");

    public static readonly Guid EMail = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E32");
}
