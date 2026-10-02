using System.Text;
using System.Text.RegularExpressions;
using SimplArchive.Application.Security;

namespace SimplArchive.UnitTests;

// XML from outside is parsed with external entities and DTDs refused EXPLICITLY (#847, A03).
//
// WHY THIS TEST EXISTS AT ALL, since nothing was reachable. `XDocument.Parse` already prohibits DTDs and uses
// a null resolver, so XXE was never available through these sites. The complaint the register made is subtler
// and correct: the control was there by FRAMEWORK DEFAULT, nothing said it was wanted, and nothing tested it.
// A later refactor to `XmlReader` -- whose default is the same but which anybody needing a feature from it
// routinely configures otherwise -- would have removed it silently and read as a tidy-up in review.
//
// So this file does two things: it proves the refusal with a real payload, and the scanner below makes the
// refusal the only way to parse untrusted XML in src/.
public partial class SafeXmlTests
{
    // A classic external-entity payload. Kept verbatim rather than built from parts, because the point is that
    // THIS, the thing an attacker actually sends, is refused.
    private const string ExternalEntity =
        """
        <?xml version="1.0"?>
        <!DOCTYPE root [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
        <root>&secret;</root>
        """;

    // Nested entity expansion -- the "billion laughs" shape. Refused by the same DTD prohibition, and listed
    // separately because it fails for a DIFFERENT reason in an implementation that allows DTDs but resolves
    // nothing: there the entities are internal, so a null resolver does not save you and only the expansion
    // cap does.
    private const string EntityExpansion =
        """
        <?xml version="1.0"?>
        <!DOCTYPE lol [
          <!ENTITY lol "lol">
          <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
          <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
        ]>
        <lol>&lol3;</lol>
        """;

    [Theory]
    [InlineData(nameof(ExternalEntity))]
    [InlineData(nameof(EntityExpansion))]
    public void A_document_declaring_a_dtd_is_refused(string which)
    {
        var xml = which == nameof(ExternalEntity) ? ExternalEntity : EntityExpansion;

        Assert.Null(SafeXml.ParseDocument(xml));
        Assert.Null(SafeXml.ParseElement(xml));
        Assert.Null(SafeXml.ParseDocument(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void Ordinary_xml_still_parses_and_keeps_its_content()
    {
        // The anti-vacuous half: a refusal that refused everything would pass the test above and break every
        // PROPFIND, every CalDAV report and the XML preview.
        var document = SafeXml.ParseDocument("""<D:propfind xmlns:D="DAV:"><D:allprop/></D:propfind>""");

        Assert.NotNull(document);
        Assert.Equal("propfind", document.Root!.Name.LocalName);
        Assert.Equal("DAV:", document.Root.Name.NamespaceName);
        Assert.Single(document.Root.Elements());

        Assert.Equal("allprop", SafeXml.ParseElement("""<D:propfind xmlns:D="DAV:"><D:allprop/></D:propfind>""")!
            .Elements().Single().Name.LocalName);
    }

    [Fact]
    public void Malformed_input_answers_null_rather_than_throwing()
    {
        // Every caller treats unusable input as "no body"; answering a protocol client with a 500 for XML it
        // chose to send is worse than the refusal it understands.
        Assert.Null(SafeXml.ParseDocument("<unclosed>"));
        Assert.Null(SafeXml.ParseElement("not xml at all"));
        Assert.Null(SafeXml.ParseDocument(string.Empty));
    }

    [GeneratedRegex(@"\b(XDocument|XElement)\.(Parse|Load)\s*\(")]
    private static partial Regex RawParse();

    [Fact]
    public void No_src_file_parses_xml_without_going_through_SafeXml()
    {
        // THE GUARD THE REGISTER ACTUALLY ASKED FOR. Pinning the behaviour of SafeXml says nothing about
        // whether anybody uses it, and the failure mode named in #847 is a NEW parse site -- or a refactor of
        // an old one -- quietly reintroducing the framework default. Scanning for the raw calls is what makes
        // the safe path the only path.
        //
        // EXEMPTED PER LINE, NOT PER FILE, and the difference is the whole point. WebDavPropFind parses the
        // request body (converted) AND re-parses a props fragment this server generated (legitimately raw):
        // allowing the FILE would have made a future raw parse of the body invisible -- the guard reporting
        // green over exactly the line it exists to catch. A marker on the line cannot do that.
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        const string marker = "safe-xml-exempt:";
        var offenders = new List<string>();
        foreach (var file in Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            // SafeXml itself is the implementation every other site routes through; exempting it by name is
            // the one file-level carve-out, because marking its own lines would read as a site that opted out.
            if (string.Equals(Path.GetFileName(file), "SafeXml.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!RawParse().IsMatch(lines[i]))
                {
                    continue;
                }

                var excused = lines[i].Contains(marker, StringComparison.Ordinal)
                    || (i > 0 && lines[i - 1].Contains(marker, StringComparison.Ordinal));
                if (!excused)
                {
                    offenders.Add($"  {Path.GetRelativePath(root, file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These lines parse XML with the framework default rather than through SafeXml, so DTDs and\n"
            + "external entities are refused only by coincidence (#847, A03). Route them through\n"
            + "SimplArchive.Application.Security.SafeXml -- or, if the input is this process's OWN output,\n"
            + $"put `{marker} <reason>` on the line or the one above it:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void The_scanner_still_recognises_a_raw_parse()
    {
        // Calibration, the lesson the rel-guard paid for: a scanner whose pattern has rotted reports a clean
        // tree forever. These are the exact spellings the three converted sites used.
        Assert.Matches(RawParse(), "var root = XDocument.Parse(body).Root;");
        Assert.Matches(RawParse(), "return XElement.Parse(text);");
        Assert.Matches(RawParse(), "XDocument.Load(reader)");
        Assert.DoesNotMatch(RawParse(), "SafeXml.ParseDocument(xml)");
    }
}
