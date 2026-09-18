using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace SimplArchive.Api.Serialization;

/// <summary>
/// The XML input formatter, with the UTC boundary normalisation the JSON side gets from
/// <see cref="UtcDateTimeOffsetConverter"/> (#1259).
/// </summary>
/// <remarks>
/// <para>
/// ADR 0548 normalises every inbound <see cref="DateTimeOffset"/> because Npgsql refuses to store one whose
/// offset is not zero — but it does so through a System.Text.Json converter, and the XML formatter runs no STJ
/// converters. So the rule held on the JSON surface and silently did not hold on the XML one, which is the
/// shape ADR 0802 calls a note rather than a rule.
/// </para>
/// <para>
/// A formatter subclass rather than an action filter walking every request: the conversion belongs to the
/// REPRESENTATION that failed to do it, so nothing pays for it on the JSON path, and a reader looking for
/// "where does XML differ from JSON" finds both halves in this folder rather than one here and one in the
/// pipeline.
/// </para>
/// </remarks>
internal sealed class UtcNormalizingXmlInputFormatter : XmlSerializerInputFormatter
{
    public UtcNormalizingXmlInputFormatter(MvcOptions options)
        : base(options)
    {
    }

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(
        InputFormatterContext context, Encoding encoding)
    {
        var result = await base.ReadRequestBodyAsync(context, encoding);

        // Only a successfully bound model: on a parse failure the model is meaningless and the error is the
        // answer the caller needs.
        if (!result.HasError)
        {
            UtcModelNormalizer.Normalize(result.Model);
        }

        return result;
    }
}
