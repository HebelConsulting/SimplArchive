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

    public string Rel { get; set; } = "";

    public string Href { get; set; } = "";

    public string Method { get; set; } = "";
}
