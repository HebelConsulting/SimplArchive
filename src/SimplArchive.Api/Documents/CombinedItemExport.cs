using System.Text;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Composes many stored <c>.vcf</c> / <c>.ics</c> items into the single file every consuming application
/// expects (#658). Pure, and deliberately conservative: item bodies pass through byte-verbatim — above all
/// their <c>UID</c>s, which are the correlation keys a later sync matches on.
/// </summary>
/// <remarks>
/// <para>
/// vCard is easy: a <c>.vcf</c> file is a legitimate stream of <c>BEGIN:VCARD…END:VCARD</c> blocks, so the
/// combination is the blocks in order.
/// </para>
/// <para>
/// iCalendar is NOT: each stored item is a complete <c>VCALENDAR</c> wrapper around one component, and the
/// combined file must be ONE wrapper holding all the components — a naïve concatenation produces a file some
/// clients accept and others reject, which is the worst outcome because it looks fine in testing. So the
/// composer unwraps each item, carries its non-<c>VTIMEZONE</c> components verbatim, and de-duplicates
/// <c>VTIMEZONE</c> blocks by <c>TZID</c> (each zone once, however many events reference it).
/// </para>
/// </remarks>
public static class CombinedItemExport
{
    public static byte[] CombineVcf(IEnumerable<byte[]> items)
    {
        var output = new StringBuilder();
        foreach (var item in items)
        {
            var text = Decode(item).Trim();
            if (text.Length > 0)
            {
                output.Append(text).Append("\r\n");
            }
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public static byte[] CombineIcs(IEnumerable<byte[]> items)
    {
        var timezones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<string>();

        foreach (var item in items)
        {
            foreach (var block in TopLevelBlocks(Decode(item)))
            {
                if (block.Name.Equals("VTIMEZONE", StringComparison.OrdinalIgnoreCase))
                {
                    // One zone definition per TZID, however many items carry it.
                    var tzid = block.Lines.FirstOrDefault(l => l.StartsWith("TZID", StringComparison.OrdinalIgnoreCase))
                        ?? Guid.NewGuid().ToString();
                    timezones.TryAdd(tzid, block.Text);
                }
                else
                {
                    components.Add(block.Text);
                }
            }
        }

        var output = new StringBuilder();
        output.Append("BEGIN:VCALENDAR\r\n");
        output.Append("VERSION:2.0\r\n");
        output.Append("PRODID:-//SimplArchive//export//EN\r\n"); // the tasks feed's PRODID family (TaskFeeds)
        foreach (var zone in timezones.Values)
        {
            output.Append(zone).Append("\r\n");
        }

        foreach (var component in components)
        {
            output.Append(component).Append("\r\n");
        }

        output.Append("END:VCALENDAR\r\n");
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace("\n", "\r\n");

    private readonly record struct Block(string Name, string Text, IReadOnlyList<string> Lines);

    // The components INSIDE each item's VCALENDAR wrapper (VEVENT, VTODO, VTIMEZONE, …), each returned as its
    // full BEGIN…END text, verbatim. Content lines are never reflowed — folding, parameters and UIDs pass
    // through untouched.
    private static IEnumerable<Block> TopLevelBlocks(string ics)
    {
        var lines = ics.Split("\r\n");
        List<string>? current = null;
        string? currentName = null;
        var depth = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line[6..].Trim();
                if (name.Equals("VCALENDAR", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the wrapper is ours to re-create, exactly once
                }

                if (current is null)
                {
                    current = [line];
                    currentName = name;
                    depth = 1;
                    continue;
                }

                depth++;
            }

            if (current is null)
            {
                continue; // a calendar-level property (VERSION, PRODID, CALSCALE) — replaced by the new wrapper's
            }

            current.Add(line);

            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (line[4..].Trim().Equals("VCALENDAR", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    yield return new Block(currentName!, string.Join("\r\n", current), current);
                    current = null;
                    currentName = null;
                }
            }
        }
    }
}
