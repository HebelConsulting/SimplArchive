// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;

namespace SimplArchive.Api.CalDav.Http;

/// <summary>Reads DAV request bodies and writes DAV responses.</summary>
public static class DavXml
{
    /// <summary>Parses the request body as XML, or null when empty/malformed (→ treat as allprop).</summary>
    public static async Task<XElement?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return XElement.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A 207 Multi-Status response carrying the document.</summary>
    public static IActionResult MultiStatus(XDocument document) => new ContentResult
    {
        StatusCode = 207,
        ContentType = "application/xml; charset=utf-8",
        Content = Serialize(document),
    };

    /// <summary>
    /// A DAV precondition failure: <c>403</c> carrying <c>&lt;D:error&gt;</c> with the violated precondition
    /// element (RFC 4918 §16), e.g. <c>DAV:supported-report</c> for a REPORT we do not implement.
    /// </summary>
    /// <remarks>
    /// The body is what makes the refusal machine-readable: a bare 403 tells a client it may not, while the
    /// precondition tells it <i>which rule</i> it broke, which is the difference between a client that can
    /// fall back and one that just fails.
    /// </remarks>
    public static IActionResult PreconditionFailure(XName precondition) => new ContentResult
    {
        StatusCode = 403,
        ContentType = "application/xml; charset=utf-8",
        Content = Serialize(new XDocument(new XElement(Xml.DavNames.Error, new XElement(precondition)))),
    };

    public static string Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
