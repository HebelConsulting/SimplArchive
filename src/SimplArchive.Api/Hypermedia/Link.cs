namespace SimplArchive.Api.Hypermedia;

// See ADR "API request/response payload shape conventions" (rel/href/method), ADR "Hypermedia envelope
// and Problem Details errors (foundation slice)". A plain mutable class with a parameterless
// constructor, not a record — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
// negotiation") needs that shape; a positional-constructor record isn't reliably deserializable by it.
public class Link
{
    public Link()
    {
    }

    public Link(string rel, string href, string method)
    {
        Rel = rel;
        Href = href;
        Method = method;
    }

    public string Rel { get; set; } = string.Empty;

    public string Href { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// A server-rendered, localized caption — present ONLY on a link meant for the generic action surface
    /// (ADR 0743): a labeled non-GET link renders as an action in both clients with no client knowledge of
    /// the rel. Null on every link a client handles natively, which is the signal that keeps the generic
    /// surface from re-rendering machinery the client already draws its own affordances for.
    /// </summary>
    public string? Label { get; set; }

    public Link(string rel, string href, string method, string label)
        : this(rel, href, method)
    {
        Label = label;
    }
}
