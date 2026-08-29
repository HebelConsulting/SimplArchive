using System.Text;
using System.Xml;
using SimplArchive.Api.CalDav.Xml;

namespace SimplArchive.Api.WebDav;

internal sealed record PropStatXml(string Href, string Status, string Props);

// The DAV: multistatus XML the PROPFIND family answers with (issue #466 moved this out of the middleware).
internal static class WebDavXml
{
    internal static async Task WriteMultiStatusAsync(HttpContext context, List<PropStatXml> responses)
    {
        // For a PROPFIND, the answer mirrors the QUESTION (issue #801): each composed propstat is partitioned
        // against the requested property set — known properties in the 200 propstat, requested-but-unknown ones
        // in a 404 propstat naming them (RFC 4918 §9.1). Filtered here because this is the one place every
        // emitter's multistatus passes through, so the tree, the special folders and the safe-save branches
        // cannot drift apart in how they honor a request. A PROPPATCH 207 answers the update it was sent, not a
        // property question, and passes through unfiltered.
        var request = string.Equals(context.Request.Method, "PROPFIND", StringComparison.OrdinalIgnoreCase)
            ? await WebDavPropFind.ReadRequestAsync(context)
            : null;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
        foreach (var r in responses)
        {
            sb.Append("<D:response>");
            sb.Append($"<D:href>{Xml(r.Href)}</D:href>");
            foreach (var (status, props) in request is null ? [(r.Status, r.Props)] : WebDavPropFind.Apply(request, r))
            {
                sb.Append("<D:propstat>");
                sb.Append($"<D:prop>{props}</D:prop>");
                sb.Append($"<D:status>{status}</D:status>");
                sb.Append("</D:propstat>");
            }

            sb.Append("</D:response>");
        }

        sb.Append("</D:multistatus>");
        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";

        // Traced HERE rather than by wrapping the response stream: this is the one place a multistatus is
        // composed, so the body is already in hand as a string and no plumbing has to be threaded through the
        // pipeline to recover it (ADR 0626).
        WebDavTrace.ResponseBody(context.RequestServices.GetRequiredService<ILogger<WebDavMiddleware>>(), context, sb.ToString());

        await context.Response.WriteAsync(sb.ToString(), context.RequestAborted);
    }

    internal static string Xml(string value) => new XmlDocument().CreateTextNode(value).OuterXml;

}
