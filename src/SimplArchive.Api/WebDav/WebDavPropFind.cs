using System.Text;
using System.Xml;
using System.Xml.Linq;
using SimplArchive.Api.CalDav.Xml;

namespace SimplArchive.Api.WebDav;

/// <summary>
/// The property set a PROPFIND asked for, applied to the gateway's composed propstats (issue #801).
/// </summary>
/// <remarks>
/// <para>
/// The gateway used to ignore the request body: whatever the client asked, every <c>207</c> carried the same
/// fixed property set inside a single <c>200 OK</c> propstat. RFC 4918 §9.1 expects the answer to mirror the
/// question — the requested-and-known properties in a <c>200</c> propstat, the requested-and-unknown ones in a
/// <c>404</c> propstat NAMING them, so a client learns "this server doesn't have that" rather than inferring it
/// from silence. #794's whole arc was the cost of a server telling clients less than the truth; this closes the
/// same gap at the property level.
/// </para>
/// <para>
/// The parsing reuses <see cref="PropRequest"/>, the DAV toolkit ported from the sister project (ADR 0621) and
/// already answering CalDAV/CardDAV this way — one parser for one question, not a second copy. The FILTERING
/// happens at <see cref="WebDavXml.WriteMultiStatusAsync"/>, the one place a WebDAV multistatus is composed, so
/// every emitter — the tree's <c>PropFor</c>, the special folders' <c>CollectionProp</c>/<c>FileProp</c>, the
/// safe-save branches — honors the request without any of them changing (the same choke-point reasoning that
/// put the trace there, ADR 0626).
/// </para>
/// </remarks>
internal static class WebDavPropFind
{
    /// <summary>
    /// Reads and parses the PROPFIND body. An empty body is <c>allprop</c> by the RFC; a body that cannot be
    /// read as a property request degrades to <c>allprop</c> too — the pre-#801 behaviour, which no client was
    /// measured to mind — but SAYS so at Warning, because a degradation the caller cannot see must be named and
    /// must name the switch that reveals more (ADR 0626).
    /// </summary>
    internal static async Task<PropRequest> ReadRequestAsync(HttpContext context)
    {
        // The Trace path buffers and rewinds the body (WebDavTrace); without Trace this is the first read.
        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        if (string.IsNullOrWhiteSpace(body))
        {
            return PropRequest.Parse(null);
        }

        var logger = context.RequestServices.GetRequiredService<ILogger<WebDavMiddleware>>();
        try
        {
            var root = XDocument.Parse(body).Root;
            var request = PropRequest.Parse(root);

            // A request that names none of prop/allprop/propname (wrong namespace, unexpected shape) gets the
            // full set rather than an empty answer: an empty 200 propstat tells a broken client "nothing here",
            // which is the very silence this change exists to remove. An empty-but-genuine <D:prop/> is
            // indistinguishable and equally well served by everything we have.
            if (!request.AllProp && !request.PropName && request.Names.Count == 0)
            {
                if (root?.Element(DavNames.Prop) is null)
                {
                    logger.LogWarning(
                        "WebDAV PROPFIND body named none of prop/allprop/propname; answering as allprop. Trace level carries the exchange (ADR 0626).");
                }

                return PropRequest.Parse(null);
            }

            return request;
        }
        catch (XmlException e)
        {
            logger.LogWarning(
                "WebDAV PROPFIND body was not well-formed XML ({Reason}); answering as allprop. Trace level carries the exchange (ADR 0626).",
                e.Message);
            return PropRequest.Parse(null);
        }
    }

    /// <summary>
    /// Partitions one composed propstat against the request: the propstats to emit, in order. <c>allprop</c>
    /// passes the composed set through byte-identically; <c>propname</c> strips the values; an explicit list
    /// yields the requested-and-known properties with their values and a <c>404 Not Found</c> propstat naming
    /// the requested-and-unknown ones.
    /// </summary>
    internal static List<(string Status, string Props)> Apply(PropRequest request, PropStatXml response)
    {
        if (request.AllProp)
        {
            return [(response.Status, response.Props)];
        }

        var known = XElement.Parse($"<D:prop xmlns:D=\"DAV:\">{response.Props}</D:prop>").Elements().ToList();
        if (request.PropName)
        {
            return [(response.Status, Fragment(known.Select(e => new XElement(e.Name))))];
        }

        var found = new List<XElement>();
        var missing = new List<XName>();
        foreach (var name in request.Names)
        {
            if (known.FirstOrDefault(e => e.Name == name) is { } value)
            {
                found.Add(value);
            }
            else
            {
                missing.Add(name);
            }
        }

        var propstats = new List<(string Status, string Props)>();
        if (found.Count > 0 || missing.Count == 0)
        {
            propstats.Add((response.Status, Fragment(found)));
        }

        if (missing.Count > 0)
        {
            propstats.Add(("HTTP/1.1 404 Not Found", Fragment(missing.Select(name => new XElement(name)))));
        }

        return propstats;
    }

    /// <summary>
    /// Serializes property elements as a fragment for the <c>D:</c>-prefixed multistatus. The wrapper carries
    /// the prefix declaration and is sliced off, so DAV: properties come out as <c>&lt;D:name&gt;</c> with no
    /// per-element declaration (the multistatus root declares <c>D:</c>); properties in other namespaces keep
    /// their own inline declarations, which LINQ to XML adds where needed.
    /// </summary>
    private static string Fragment(IEnumerable<XElement> elements)
    {
        var wrapper = new XElement(DavNames.Prop, new XAttribute(XNamespace.Xmlns + "D", DavNames.Dav.NamespaceName), elements);
        if (!wrapper.HasElements)
        {
            return string.Empty;
        }

        var xml = wrapper.ToString(SaveOptions.DisableFormatting);
        return xml[(xml.IndexOf('>') + 1)..xml.LastIndexOf('<')];
    }
}
