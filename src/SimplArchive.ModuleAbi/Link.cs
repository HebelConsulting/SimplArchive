// Moved into the ABI from SimplArchive.Api.Hypermedia (ADR 0737): a module controller must emit the
// SAME wire envelope as a core one, and one definition is how the two cannot drift. The Api re-exposes
// these under their old simple names via global using aliases, so its hundred-odd resource DTOs read
// unchanged.
namespace SimplArchive.ModuleAbi;

// See ADR "API request/response payload shape conventions" (rel/href/method), ADR "Hypermedia envelope
// and Problem Details errors (foundation slice)". A plain mutable class with a parameterless
// constructor, not a record — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
// negotiation") needs that shape; a positional-constructor record isn't reliably deserializable by it.
/// <summary>One hypermedia link: a rel the client navigates by, the href it follows, the method it uses.</summary>
public class Link
{
    /// <summary>For the serializers (XmlSerializer needs a parameterless constructor).</summary>
    public Link()
    {
    }

    /// <summary>A link a client handles natively — no label, so the generic action surface skips it.</summary>
    public Link(string rel, string href, string method)
    {
        Rel = rel;
        Href = href;
        Method = method;
    }

    /// <summary>The relation name — the compatibility surface a client navigates by (ADR 0543).</summary>
    public string Rel { get; set; } = string.Empty;

    /// <summary>The address the rel reaches; clients follow it, never compose it.</summary>
    public string Href { get; set; } = string.Empty;

    /// <summary>The HTTP method that performs the action (ADR 0719: the method says which).</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// A server-rendered, localized caption — present ONLY on a link meant for the generic action surface
    /// (ADR 0743): a labeled non-GET link renders as an action in both clients with no client knowledge of
    /// the rel. Null on every link a client handles natively, which is the signal that keeps the generic
    /// surface from re-rendering machinery the client already draws its own affordances for.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>A LABELED link — an action for the generic surface (ADR 0743), rendered by caption.</summary>
    public Link(string rel, string href, string method, string label)
        : this(rel, href, method)
    {
        Label = label;
    }
}
