// Moved into the ABI from SimplArchive.Api.Hypermedia (ADR 0737): a module controller must emit the
// SAME wire envelope as a core one, and one definition is how the two cannot drift. The Api re-exposes
// these under their old simple names via global using aliases, so its hundred-odd resource DTOs read
// unchanged.
namespace SimplArchive.ModuleAbi;

// A resource's links live alongside its own fields, flat — not nested under a wrapper key like HAL's
// _links/_embedded, which ADR "API request/response payload shape conventions" explicitly rejected
// (JSON-first, no clean XML mapping). Concrete resource DTOs inherit from this and add their own fields
// directly. See ADR "Hypermedia envelope and Problem Details errors (foundation slice)". A plain mutable
// get/set property, not { get; init; } — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
// negotiation") needs a settable property, not an init-only one.
/// <summary>The envelope every resource rides in: its own fields flat, plus its links (ADR 0543).</summary>
public abstract class HypermediaResource
{
    /// <summary>The rels this resource advertises — presence IS the affordance (ADR 0543).</summary>
    public List<Link> Links { get; set; } = [];
}
