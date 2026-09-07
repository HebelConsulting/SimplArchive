using System.Text;

namespace SimplArchive.Presentation;

/// <summary>
/// How a machine status NAME renders (#1062) — shared by both clients (ADR 0650: the answer both must give
/// identically). Status names are code-shaped ("PassengerCurrent", "NightPassengerCurrent", "WindowOk");
/// the display splits the words and keeps only the leading capital: "Passenger current". Acronym runs
/// survive intact ("OK", "VFR") — splitting inside them would produce "O k".
/// </summary>
public static class MachineStatusDisplay
{
    public static string Pretty(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var startsWord = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
            if (startsWord)
            {
                sb.Append(' ');
                // Lower-case the word's initial unless it begins an acronym run ("Ok" → "OK" stays as-is
                // only when followed by another upper; a lone capital starting a word reads as prose).
                sb.Append(i + 1 < name.Length && char.IsUpper(name[i + 1]) ? c : char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
