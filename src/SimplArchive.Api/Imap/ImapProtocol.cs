using System.Text;

namespace SimplArchive.Api.Imap;

// Wire-format helpers for the hand-rolled IMAP4rev1 subset (ADR "IMAP endpoint (read-only, first slice)"):
// argument tokenizing, quoted-string escaping, and RFC 3501 §5.1.3 modified UTF-7 for mailbox names (folder
// names are user data — umlauts must survive LIST/SELECT round trips with real clients).
internal static class ImapProtocol
{
    /// <summary>Splits an argument string into atoms / quoted strings / parenthesized groups (kept whole).</summary>
    internal static List<string> Tokenize(string arguments)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < arguments.Length)
        {
            switch (arguments[i])
            {
                case ' ':
                    i++;
                    break;
                case '"':
                    {
                        var sb = new StringBuilder();
                        i++;
                        while (i < arguments.Length && arguments[i] != '"')
                        {
                            if (arguments[i] == '\\' && i + 1 < arguments.Length)
                            {
                                i++;
                            }

                            sb.Append(arguments[i]);
                            i++;
                        }

                        i++; // closing quote
                        tokens.Add(sb.ToString());
                        break;
                    }
                case '(':
                    {
                        // A parenthesized group travels as ONE token (FETCH item lists, STATUS item lists).
                        var depth = 0;
                        var start = i;
                        while (i < arguments.Length)
                        {
                            depth += arguments[i] switch { '(' => 1, ')' => -1, _ => 0 };
                            i++;
                            if (depth == 0)
                            {
                                break;
                            }
                        }

                        tokens.Add(arguments[start..i]);
                        break;
                    }
                default:
                    {
                        var start = i;
                        while (i < arguments.Length && arguments[i] != ' ')
                        {
                            i++;
                        }

                        tokens.Add(arguments[start..i]);
                        break;
                    }
            }
        }

        return tokens;
    }

    /// <summary>The byte count of a trailing {n} literal marker, or null when the line carries none.</summary>
    internal static int? TrailingLiteralLength(string line)
    {
        if (!line.EndsWith('}'))
        {
            return null;
        }

        var open = line.LastIndexOf('{');
        var inner = open >= 0 ? line[(open + 1)..^1].TrimEnd('+') : "";
        return open >= 0 && int.TryParse(inner, out var n) && n >= 0 ? n : null;
    }

    /// <summary>A mailbox name as it travels on the wire: modified-UTF-7-encoded and quoted.</summary>
    internal static string QuoteMailbox(string name) =>
        $"\"{EncodeModifiedUtf7(name).Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    // ---- RFC 3501 §5.1.3 modified UTF-7 --------------------------------------------------------------
    // Printable ASCII travels as-is, '&' as "&-", anything else as '&' + base64(UTF-16BE, '+'→',', unpadded) + '-'.

    internal static string EncodeModifiedUtf7(string value)
    {
        var sb = new StringBuilder();
        var run = new List<byte>();

        void FlushRun()
        {
            if (run.Count == 0)
            {
                return;
            }

            sb.Append('&').Append(Convert.ToBase64String(run.ToArray()).TrimEnd('=').Replace('/', ',')).Append('-');
            run.Clear();
        }

        foreach (var c in value)
        {
            if (c is >= '\x20' and <= '\x7e')
            {
                FlushRun();
                sb.Append(c == '&' ? "&-" : c.ToString());
            }
            else
            {
                run.Add((byte)(c >> 8));
                run.Add((byte)(c & 0xff));
            }
        }

        FlushRun();
        return sb.ToString();
    }

    internal static string DecodeModifiedUtf7(string value)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < value.Length)
        {
            if (value[i] != '&')
            {
                sb.Append(value[i]);
                i++;
                continue;
            }

            var end = value.IndexOf('-', i + 1);
            if (end < 0)
            {
                sb.Append(value[i..]);
                break;
            }

            if (end == i + 1)
            {
                sb.Append('&'); // "&-" is a literal ampersand
            }
            else
            {
                var b64 = value[(i + 1)..end].Replace(',', '/');
                var padded = b64 + new string('=', (4 - b64.Length % 4) % 4);
                var bytes = Convert.FromBase64String(padded);
                for (var k = 0; k + 1 < bytes.Length; k += 2)
                {
                    sb.Append((char)((bytes[k] << 8) | bytes[k + 1]));
                }
            }

            i = end + 1;
        }

        return sb.ToString();
    }
}
