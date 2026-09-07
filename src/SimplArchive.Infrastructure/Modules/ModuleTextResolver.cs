using System.Globalization;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Resolves a module's localized template for a refusal or status-diagnosis code (ABI 0.10, ADR 0767).
/// Pure lookups over the loaded modules' catalogs: culture (two-letter) → "en" → null, and for refusals
/// "CODE.arg0" before "CODE" — the trick that lets a role-dependent sentence localize whole. A null answer
/// means "no catalog entry": the caller keeps the composed invariant text, so a gap degrades to today's
/// behaviour, never to silence.
/// </summary>
public static class ModuleTextResolver
{
    /// <summary>The template for <paramref name="code"/> from <paramref name="moduleId"/>'s catalog — or,
    /// when the acting module is unknown (an exception that bubbled without scope), from whichever loaded
    /// module defines the code; module code prefixes make a collision a naming bug, not a runtime risk.</summary>
    public static (string Template, string ModuleId)? Resolve(
        IEnumerable<ModuleLoader.LoadedModule> modules, string? moduleId, string code, string? arg0, CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName;
        foreach (var loaded in modules)
        {
            if (moduleId is not null && !string.Equals(loaded.Module.ModuleId, moduleId, StringComparison.Ordinal))
            {
                continue;
            }

            var catalog = loaded.Module.LocalizedTexts;
            foreach (var lang in new[] { language, "en" }.Distinct())
            {
                if (!catalog.TryGetValue(lang, out var texts))
                {
                    continue;
                }

                if (arg0 is not null && texts.TryGetValue($"{code}.{arg0}", out var roleTemplate))
                {
                    return (roleTemplate, loaded.Module.ModuleId);
                }

                if (texts.TryGetValue(code, out var template))
                {
                    return (template, loaded.Module.ModuleId);
                }
            }
        }

        return null;
    }

    /// <summary>Formats a refusal template with its args — tolerant of surplus slots (a template asking for
    /// more than the refusal carried keeps the placeholder visible rather than throwing at error time).</summary>
    public static string Format(string template, IReadOnlyList<string> args)
    {
        var text = template;
        for (var i = 0; i < args.Count; i++)
        {
            text = text.Replace($"{{{i}}}", args[i], StringComparison.Ordinal);
        }

        return text;
    }
}
