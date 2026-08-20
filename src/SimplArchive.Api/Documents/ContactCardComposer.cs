using System.Text;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Lossless structured read/merge of a contact vCard — ported from SimplCalCon (its ADR 0082), per the
/// standing pattern of porting the DAV layer rather than re-deriving it (ADR 0621). Works at the vCard line level: on
/// <see cref="Merge"/> it drops only the property lines the form models and re-emits the rest verbatim,
/// so PHOTO, X-*, IMPP, CATEGORIES and any other properties (and their folding) survive an edit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately a text merge, not the vCard library's writer.</b> Parsing into the library's model and
/// re-serialising looks equivalent and is not: it rebuilds the card from what the model knows, silently
/// dropping PHOTO, ADR, NOTE, BDAY, URL, the TYPE parameters on e-mails and phones, and every X-* extension.
/// SimplCalCon shipped that mistake first and replaced it; this port exists so we do not repeat it. Keeping
/// the merge at the line level also keeps TYPE handling under our control.
/// </para>
/// <para>
/// <b>The limit of "lossless".</b> Properties the form does not model are untouched. Properties it DOES model
/// are normalised on save — a TYPE outside home/work/mobile is rewritten to none, and re-emitted
/// EMAIL/TEL/ADR take our canonical form. A card is only ever rewritten when a user saves the form.
/// </para>
/// <para>
/// The merged blob is validated by the ordinary classification path on write, so a malformed merge is
/// rejected rather than persisted.
/// </para>
/// </remarks>
public sealed class ContactCardComposer : IContactCardComposer
{
    // vCard properties the structured form owns (case-insensitive); everything else is preserved verbatim.
    private static readonly HashSet<string> ModelledProperties =
        new(StringComparer.OrdinalIgnoreCase) { "FN", "N", "ORG", "TITLE", "EMAIL", "TEL", "ADR", "BDAY", "URL", "NOTE" };

    // What a decoded photo is allowed to BE. A vCard is user-supplied data, so the type it declares is a
    // suggestion, not a fact — and `image/svg+xml` is a scriptable document, which is why an allowlist of raster
    // formats is the rule rather than "whatever it says". Anything else is treated as no photo at all: showing
    // initials is a smaller loss than echoing an attacker's document back from our own origin.
    private static readonly (string Type, byte[] Magic)[] PhotoTypes =
    [
        ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        ("image/png", [0x89, 0x50, 0x4E, 0x47]),
        ("image/gif", [0x47, 0x49, 0x46, 0x38]),
        ("image/webp", [0x52, 0x49, 0x46, 0x46]),
    ];

    public ContactPhoto? ReadPhoto(string blob)
    {
        if (string.IsNullOrWhiteSpace(blob))
        {
            return null;
        }

        var photo = LogicalLines(blob).Select(ParseLine).FirstOrDefault(l => Is(l, "PHOTO"));
        if (photo is null)
        {
            return null;
        }

        var value = photo.RawValue.Trim();

        // A `data:` URI carries its own media type; strip the header and keep the base64 tail. Anything else
        // that looks like a URI is an EXTERNAL reference, which is deliberately not followed (see the interface).
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            value = comma < 0 ? string.Empty : value[(comma + 1)..];
        }
        else if (value.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        // vCard 3.0 spells inline data as ENCODING=b (or BASE64); a value with neither is a plain URI form we
        // do not follow. Folded lines have already been rejoined by LogicalLines, but the whitespace of the
        // folding itself is not part of the payload.
        var bytes = Decode(value.Replace(" ", string.Empty).Replace("\t", string.Empty));

        // The type is SNIFFED, never read from the card's own TYPE parameter — see PhotoTypes.
        return bytes is { Length: > 0 } && Sniff(bytes) is { } contentType
            ? new ContactPhoto(bytes, contentType)
            : null;
    }

    private static byte[]? Decode(string base64)
    {
        Span<byte> buffer = new byte[((base64.Length / 4) + 1) * 3];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? buffer[..written].ToArray() : null;
    }

    private static string? Sniff(byte[] bytes) =>
        PhotoTypes.FirstOrDefault(t => bytes.Length >= t.Magic.Length && bytes.Take(t.Magic.Length).SequenceEqual(t.Magic))
            .Type;

    public ContactCard Read(string blob)
    {
        var lines = LogicalLines(blob).Select(ParseLine).ToList();

        string? First(string name) => lines.FirstOrDefault(l => Is(l, name))?.Value;

        var n = lines.FirstOrDefault(l => Is(l, "N"));
        var nameParts = n is null ? [] : SplitComponents(n.RawValue);
        var adr = lines.FirstOrDefault(l => Is(l, "ADR"));

        return new ContactCard(
            FormattedName: First("FN"),
            FamilyName: Component(nameParts, 0),
            GivenName: Component(nameParts, 1),
            Organization: OrgName(lines.FirstOrDefault(l => Is(l, "ORG"))),
            Title: First("TITLE"),
            Emails: lines.Where(l => Is(l, "EMAIL")).Select(l => new ContactField(l.Value, TypeOf(l))).ToList(),
            Phones: lines.Where(l => Is(l, "TEL")).Select(l => new ContactField(l.Value, TypeOf(l))).ToList(),
            Addresses: lines.Where(l => Is(l, "ADR")).Select(ToAddress).ToList(),
            Birthday: DatePart(First("BDAY")),
            Url: First("URL"),
            Note: First("NOTE"));
    }

    public string Merge(string? existingBlob, ContactCard card, string uid)
    {
        // Split the existing card into (raw) logical lines; keep everything that isn't a modelled property
        // or a structural line we re-emit ourselves (BEGIN/END/UID).
        var kept = new List<string>();
        string? version = null;
        if (!string.IsNullOrWhiteSpace(existingBlob))
        {
            foreach (var raw in LogicalLines(existingBlob))
            {
                var name = PropertyName(raw);
                if (name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("END", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("UID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.Equals("VERSION", StringComparison.OrdinalIgnoreCase))
                {
                    version = raw;
                    continue;
                }

                if (!ModelledProperties.Contains(name))
                {
                    kept.Add(raw);   // preserved verbatim (PHOTO, X-*, …), folding intact
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("BEGIN:VCARD\r\n");
        sb.Append(version is null ? "VERSION:3.0" : version).Append("\r\n");
        sb.Append("UID:").Append(uid).Append("\r\n");

        AppendLine(sb, "FN", card.FormattedName ?? FullName(card) ?? "");
        sb.Append("N:").Append(Escape(card.FamilyName)).Append(';').Append(Escape(card.GivenName)).Append(";;;\r\n");
        if (!string.IsNullOrWhiteSpace(card.Organization)) { AppendLine(sb, "ORG", card.Organization!); }
        if (!string.IsNullOrWhiteSpace(card.Title)) { AppendLine(sb, "TITLE", card.Title!); }

        foreach (var email in card.Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value)))
        {
            AppendTyped(sb, "EMAIL", email.Type, email.Value);
        }

        foreach (var phone in card.Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value)))
        {
            AppendTyped(sb, "TEL", phone.Type, phone.Value);
        }

        foreach (var a in card.Addresses.Where(a => !IsEmptyAddress(a)))
        {
            var value = $";;{Escape(a.Street)};{Escape(a.City)};{Escape(a.Region)};{Escape(a.PostalCode)};{Escape(a.Country)}";
            sb.Append("ADR").Append(TypeParam(a.Type)).Append(':').Append(value).Append("\r\n");
        }

        if (!string.IsNullOrWhiteSpace(card.Birthday)) { AppendLine(sb, "BDAY", card.Birthday!); }
        if (!string.IsNullOrWhiteSpace(card.Url)) { AppendLine(sb, "URL", card.Url!); }
        if (!string.IsNullOrWhiteSpace(card.Note)) { AppendLine(sb, "NOTE", card.Note!); }

        foreach (var raw in kept)
        {
            sb.Append(raw).Append("\r\n");
        }

        sb.Append("END:VCARD\r\n");
        return sb.ToString();
    }

    // --- vCard line helpers ---

    private sealed record Line(string Name, string Params, string RawValue)
    {
        public string Value => Unescape(RawValue);
    }

    // Splits into logical (unfolded) lines, preserving each line's original text (for verbatim re-emit).
    private static IEnumerable<string> LogicalLines(string blob)
    {
        var raw = blob.Replace("\r\n", "\n").Split('\n');
        string? current = null;
        foreach (var line in raw)
        {
            if (line.Length == 0) { continue; }
            if ((line[0] == ' ' || line[0] == '\t') && current is not null)
            {
                current += line[1..];   // RFC 6350 unfolding: drop the single leading WSP
            }
            else
            {
                if (current is not null) { yield return current; }
                current = line;
            }
        }

        if (current is not null) { yield return current; }
    }

    private static string PropertyName(string rawLine)
    {
        var end = rawLine.IndexOfAny([';', ':']);
        return end < 0 ? rawLine : rawLine[..end];
    }

    private static Line ParseLine(string rawLine)
    {
        var colon = rawLine.IndexOf(':');
        var head = colon < 0 ? rawLine : rawLine[..colon];
        var value = colon < 0 ? string.Empty : rawLine[(colon + 1)..];
        var semi = head.IndexOf(';');
        var name = semi < 0 ? head : head[..semi];
        var pars = semi < 0 ? string.Empty : head[(semi + 1)..];
        return new Line(name, pars, value);
    }

    private static bool Is(Line line, string name) => line.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? TypeOf(Line line)
    {
        // TYPE=work / TYPE=HOME,VOICE / type=cell — normalise to home/work/mobile.
        foreach (var p in line.Params.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!p.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase)) { continue; }
            foreach (var t in p[5..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var v = t.Trim().Trim('"').ToLowerInvariant();
                if (v is "cell" or "mobile") { return "mobile"; }
                if (v is "work") { return "work"; }
                if (v is "home") { return "home"; }
            }
        }

        return null;
    }

    private static ContactAddress ToAddress(Line line)
    {
        var c = SplitComponents(line.RawValue);
        return new ContactAddress(
            TypeOf(line), Component(c, 2), Component(c, 3), Component(c, 4), Component(c, 5), Component(c, 6));
    }

    private static string? OrgName(Line? org) => org is null ? null : Component(SplitComponents(org.RawValue), 0);

    private static string? DatePart(string? bday) =>
        string.IsNullOrWhiteSpace(bday) ? null : (bday.Length >= 10 ? bday[..10] : bday);

    private static List<string> SplitComponents(string rawValue)
    {
        // Split on unescaped ';' then unescape each component.
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < rawValue.Length; i++)
        {
            if (rawValue[i] == '\\' && i + 1 < rawValue.Length) { sb.Append(rawValue[i]).Append(rawValue[++i]); }
            else if (rawValue[i] == ';') { parts.Add(sb.ToString()); sb.Clear(); }
            else { sb.Append(rawValue[i]); }
        }

        parts.Add(sb.ToString());
        return parts.Select(Unescape).ToList();
    }

    private static string? Component(IReadOnlyList<string> parts, int index) =>
        index < parts.Count && !string.IsNullOrWhiteSpace(parts[index]) ? parts[index] : null;

    private static bool IsEmptyAddress(ContactAddress a) =>
        string.IsNullOrWhiteSpace(a.Street) && string.IsNullOrWhiteSpace(a.City) && string.IsNullOrWhiteSpace(a.Region)
        && string.IsNullOrWhiteSpace(a.PostalCode) && string.IsNullOrWhiteSpace(a.Country);

    private static string? FullName(ContactCard c)
    {
        var joined = string.Join(' ', new[] { c.GivenName, c.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return joined.Length == 0 ? null : joined;
    }

    private static void AppendLine(StringBuilder sb, string name, string value) =>
        sb.Append(name).Append(':').Append(Escape(value)).Append("\r\n");

    private static void AppendTyped(StringBuilder sb, string name, string? type, string value) =>
        sb.Append(name).Append(TypeParam(type)).Append(':').Append(Escape(value)).Append("\r\n");

    private static string TypeParam(string? type) => type switch
    {
        "mobile" => ";TYPE=CELL",
        "work" => ";TYPE=WORK",
        "home" => ";TYPE=HOME",
        _ => string.Empty,
    };

    private static string Escape(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) { return value; }
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch { 'n' or 'N' => '\n', _ => next });
            }
            else { sb.Append(value[i]); }
        }

        return sb.ToString();
    }
}
