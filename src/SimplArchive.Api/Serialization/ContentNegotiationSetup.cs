using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace SimplArchive.Api.Serialization;

/// <summary>
/// Wires up JSON/XML content negotiation: the vendor media types each formatter answers to (ADR 0190), and the
/// XML reader that normalises inbound timestamps the way the JSON converter does (#1259).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>Program.cs</c> rather than left inline. It had grown to a third of a screen of formatter
/// plumbing inside the service registration, and adding the XML swap took that file over the 1000-line limit —
/// which is the rule doing its job: the block is a cohesive responsibility with a name, so it gets a home next
/// to the converters it configures instead of an exemption.
/// </para>
/// </remarks>
internal static class ContentNegotiationSetup
{
    private const string Json = "application/vnd.simplarchive.v1+json";
    private const string Xml = "application/vnd.simplarchive.v1+xml";

    /// <summary>Adds the vendor media types and installs the UTC-normalising XML input formatter.</summary>
    public static void Configure(MvcOptions options)
    {
        // The vendor+version media type is added to each formatter's own SupportedMediaTypes, so ASP.NET Core's
        // built-in negotiation picks the right formatter with no header rewriting. Only v1 exists today.
        foreach (var formatter in options.OutputFormatters)
        {
            if (formatter is SystemTextJsonOutputFormatter)
            {
                ((TextOutputFormatter)formatter).SupportedMediaTypes.Add(Json);
            }
            else if (formatter is XmlSerializerOutputFormatter)
            {
                ((TextOutputFormatter)formatter).SupportedMediaTypes.Add(Xml);
            }
        }

        foreach (var formatter in options.InputFormatters)
        {
            if (formatter is SystemTextJsonInputFormatter)
            {
                ((TextInputFormatter)formatter).SupportedMediaTypes.Add(Json);
            }
            else if (formatter is XmlSerializerInputFormatter)
            {
                ((TextInputFormatter)formatter).SupportedMediaTypes.Add(Xml);
            }
        }

        // The XML reader is SWAPPED for one that normalises inbound timestamps to UTC (#1259). ADR 0548's
        // converter lives inside System.Text.Json, and the XML formatter runs no STJ converters — so the rule
        // "the API deals in instants" held on one representation and silently did not on the other, which is
        // exactly the note-not-a-rule shape ADR 0802 exists to prevent.
        //
        // Replaced IN PLACE, after the media types are registered, so the replacement inherits them — including
        // the vendor one, without which an `application/vnd.simplarchive.v1+xml` request would stop binding at
        // all and the fix would break the very representation it exists to serve.
        for (var i = 0; i < options.InputFormatters.Count; i++)
        {
            if (options.InputFormatters[i] is XmlSerializerInputFormatter and not UtcNormalizingXmlInputFormatter)
            {
                var replacement = new UtcNormalizingXmlInputFormatter(options);
                replacement.SupportedMediaTypes.Clear();
                foreach (var mediaType in ((TextInputFormatter)options.InputFormatters[i]).SupportedMediaTypes)
                {
                    replacement.SupportedMediaTypes.Add(mediaType);
                }

                options.InputFormatters[i] = replacement;
            }
        }
    }
}
