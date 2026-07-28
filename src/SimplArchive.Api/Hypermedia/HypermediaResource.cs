namespace SimplArchive.Api.Hypermedia;

// A resource's links live alongside its own fields, flat — not nested under a wrapper key like HAL's
// _links/_embedded, which ADR "API request/response payload shape conventions" explicitly rejected
// (JSON-first, no clean XML mapping). Concrete resource DTOs inherit from this and add their own fields
// directly. See ADR "Hypermedia envelope and Problem Details errors (foundation slice)". A plain mutable
// get/set property, not { get; init; } — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
// negotiation") needs a settable property, not an init-only one.
public abstract class HypermediaResource
{
    public List<Link> Links { get; set; } = [];
}
