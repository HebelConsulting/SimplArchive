namespace SimplArchive.Presentation;

/// <summary>
/// What a module-settings form sends when the administrator saves it (ADR 0772) — the one rule both clients
/// must answer identically, so it is answered once here.
/// </summary>
/// <remarks>
/// <para>
/// The rule is not obvious and gets it wrong in a way nobody notices: a SECRET's value never crosses the wire
/// back to the client, so its box starts empty even when one is configured. Sending that empty box would
/// <b>clear a credential the user never touched</b> — the form would look like it saved an endpoint and
/// silently destroy the password beside it. So an untouched secret is <b>omitted</b>, which the server's
/// merge reads as "leave it alone", while an emptied PLAIN field is sent as null, which clears it.
/// </para>
/// <para>
/// Shared rather than written twice because the failure is invisible from the client that has it wrong: the
/// save succeeds, the form reloads, and only the next call that needs the credential fails.
/// </para>
/// </remarks>
public static class ModuleSettingsForm
{
    /// <summary>One field as the form holds it: what was declared, whether a value already exists, and what
    /// the user has typed.</summary>
    /// <param name="Key">The declared setting key.</param>
    /// <param name="IsSecret">Whether the value is a credential — the whole reason this rule exists.</param>
    /// <param name="Entry">What is in the box now. Empty means "untouched" for a secret, "cleared" otherwise.</param>
    public readonly record struct Field(string Key, bool IsSecret, string Entry);

    /// <summary>
    /// The key → value map to PUT: every field the user meant to change, and nothing else.
    /// </summary>
    public static Dictionary<string, string?> ValuesToSend(IEnumerable<Field> fields)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var typed = field.Entry ?? string.Empty;

            // An untouched secret is omitted — see the remarks; this single line is the rule.
            if (field.IsSecret && typed.Length == 0)
            {
                continue;
            }

            values[field.Key] = typed.Length == 0 ? null : typed;
        }

        return values;
    }
}
