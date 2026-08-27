using System.Text;
using System.Xml;

namespace SimplArchive.Api.WebDav;

internal sealed record PropStatXml(string Href, string Status, string Props);

// The DAV: multistatus XML the PROPFIND family answers with (issue #466 moved this out of the middleware).
internal static class WebDavXml
{
    internal static async Task WriteMultiStatusAsync(HttpContext context, List<PropStatXml> responses)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
        foreach (var r in responses)
        {
            sb.Append("<D:response>");
            sb.Append($"<D:href>{Xml(r.Href)}</D:href>");
            sb.Append("<D:propstat>");
            sb.Append($"<D:prop>{r.Props}</D:prop>");
            sb.Append($"<D:status>{r.Status}</D:status>");
            sb.Append("</D:propstat></D:response>");
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
