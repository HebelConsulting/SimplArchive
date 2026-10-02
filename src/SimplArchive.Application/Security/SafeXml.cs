using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SimplArchive.Application.Security;

/// <summary>
/// Parses XML that came from outside, with external entities and DTDs refused EXPLICITLY (#847, A03).
/// </summary>
/// <remarks>
/// <para>
/// <b>This does not fix a reachable hole — it stops one becoming reachable.</b> <see cref="XDocument.Parse(string)"/>
/// already prohibits DTDs and uses a null resolver, so XXE was not available through the sites this replaces.
/// But the control was present by FRAMEWORK DEFAULT rather than by assertion: nothing said it was wanted,
/// nothing tested it, and a later refactor to <see cref="XmlReader"/> — whose own default is
/// <see cref="DtdProcessing.Prohibit"/> but which is routinely configured otherwise by anyone who needs a
/// feature from it — would have removed it silently and looked like a tidy-up in review.
/// </para>
/// <para>
/// <b>What a reachable version costs</b>, which is why it is worth asserting rather than noting: an external
/// entity is read by the SERVER, with the server's filesystem and network reach. A <c>PROPFIND</c> body is
/// the shape that matters here — it arrives unauthenticated-adjacent on a protocol endpoint, and its answer
/// is echoed back, so an entity resolving to a local file is an exfiltration channel rather than merely a
/// crash.
/// </para>
/// <para>
/// <b>Three settings, and each one is load-bearing.</b> <see cref="DtdProcessing.Prohibit"/> refuses the
/// declaration an entity needs; a null <see cref="XmlReaderSettings.XmlResolver"/> means that even a DTD
/// reached another way resolves nothing; and <see cref="XmlReaderSettings.MaxCharactersFromEntities"/> caps
/// the expansion that makes a "billion laughs" an out-of-memory rather than a parse error. Setting only the
/// first is the common half-fix.
/// </para>
/// <para>
/// <b>Returns null rather than throwing</b>, because every caller here already treats malformed input as
/// "no usable body" and answering a protocol client with a 500 for XML it chose to send is a worse outcome
/// than the refusal it understands.
/// </para>
/// </remarks>
public static class SafeXml
{
    // Shared, and deliberately not exposed: a caller that could hand in its own settings is a caller that can
    // undo the three decisions above, which is the thing this type exists to prevent.
    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        CloseInput = true,
    };

    /// <summary>Parses a whole document, or null when it is not well-formed XML this will accept.</summary>
    public static XDocument? ParseDocument(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings());
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Parses a single element, or null — the shape the protocol bodies arrive in.</summary>
    public static XElement? ParseElement(string xml) => ParseDocument(xml)?.Root;

    /// <summary>Parses bytes, decoding as UTF-8. Used where the input is a stored object rather than a body.</summary>
    public static XDocument? ParseDocument(byte[] utf8) => ParseDocument(Encoding.UTF8.GetString(utf8));
}
