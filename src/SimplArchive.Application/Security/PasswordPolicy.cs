using System.Reflection;
using System.Text;

namespace SimplArchive.Application.Security;

/// <summary>Why a chosen password was refused — one reason per fix, because the fixes differ.</summary>
public enum PasswordRefusal
{
    /// <summary>Shorter than <see cref="PasswordPolicy.MinimumLength"/>.</summary>
    TooShort,

    /// <summary>Longer than <see cref="PasswordPolicy.MaximumLength"/> — a denial-of-service guard, not a rule.</summary>
    TooLong,

    /// <summary>On a breach list, or a decorated form of something on one.</summary>
    Common,
}

/// <summary>
/// The rule for a CHOSEN password (#849, ADR 0065's other half).
/// </summary>
/// <remarks>
/// <para>
/// <b>NIST 800-63B, including the parts that say what NOT to do.</b> A minimum length and a breach check; no
/// composition rules, no expiry, no history — NIST argues against all three, because "must contain a symbol"
/// produces predictable substitutions rather than entropy, and forced rotation produces the same password with
/// the month on the end. #843 built the throttle (ADR 0716), which bounds how fast an attacker may guess; this
/// is the other half, and the two are complementary: a wall in front of a door does not improve the lock.
/// </para>
/// <para>
/// <b>Scope is the CHOSEN password only.</b> The generated app-passwords (WebDAV, IMAP) and the generated
/// administrative reset are 18 random bytes and are not the exposure; the one a person picks is, because it
/// reaches every surface.
/// </para>
/// <para>
/// <b>Why the lookup is on a NORMALISED form, which is the whole design.</b> Measured when the list was
/// bundled: only 10 of its 10,001 entries are 12 characters or longer, so at a 12-character minimum a verbatim
/// lookup refuses essentially nothing the length rule has not already refused. Two controls that overlap
/// completely are one control and one decoration. Normalising first — case, leet substitutions, trailing
/// decoration — is what makes the list bite, because it catches what people actually choose when told to pick
/// twelve characters with a number in it: <c>Summer2026!</c>, <c>P@ssw0rd2026</c>, <c>Basketball2026</c>.
/// </para>
/// <para>
/// <b>The refusal is not an oracle.</b> Every caller is already authenticated — a user changing their own
/// password, or an administrator creating an account — so naming the reason reveals nothing about who exists.
/// It must be named, or the person cannot act on it.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// Twelve, above NIST's stated floor of eight.
    /// </summary>
    /// <remarks>
    /// This is the one credential that reaches every surface — web, desktop, WebDAV, IMAP — and eight characters
    /// is within reach of offline cracking if a hash ever leaks. Existing passwords are unaffected: nothing
    /// re-validates at login, which is deliberate, since locking people out of an archive to enforce a rule
    /// they were never offered is a worse outcome than the rule arriving with the next change.
    /// </remarks>
    public const int MinimumLength = 12;

    /// <summary>
    /// A generous ceiling, present only so a megabyte of input cannot be hashed on demand.
    /// </summary>
    /// <remarks>
    /// NIST asks for a generous maximum rather than a tight one, because truncating or refusing long passwords
    /// punishes exactly the people doing it right (a passphrase, a password manager's output).
    /// </remarks>
    public const int MaximumLength = 256;

    private static readonly Lazy<HashSet<string>> Common = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The refusal, or null when the password is acceptable.</summary>
    public static PasswordRefusal? Refusal(string? password)
    {
        if (password is null || password.Length < MinimumLength)
        {
            return PasswordRefusal.TooShort;
        }

        if (password.Length > MaximumLength)
        {
            return PasswordRefusal.TooLong;
        }

        // The verbatim form first: a long password that is itself on the list needs no interpretation.
        if (Common.Value.Contains(password.ToLowerInvariant()))
        {
            return PasswordRefusal.Common;
        }

        var normalised = Normalise(password);

        // An EMPTY normalisation means the password was decoration all the way down — "123456789012",
        // "!!!!!!!!!!!!". Refused as common rather than treated as a miss, which is what a lookup of the empty
        // string would otherwise be.
        return normalised.Length == 0 || Common.Value.Contains(normalised)
            ? PasswordRefusal.Common
            : null;
    }

    /// <summary>
    /// Reduces a password to the word somebody started from, so a decorated common password is still common.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately conservative, because over-normalising refuses strong passwords.</b> It folds case,
    /// strips decoration from the ENDS only, and then undoes the leet substitutions that are genuinely
    /// predictable. It does not remove interior characters, so a passphrase keeps its shape:
    /// <c>correct-horse-battery</c> does not collapse to anything on a list.
    /// </para>
    /// <para>
    /// <c>!</c> is deliberately NOT read as <c>i</c> or <c>l</c>. It is the single most common trailing
    /// decoration, where it means nothing, and treating it as a letter mangles the middle of passwords that
    /// merely contain one.
    /// </para>
    /// </remarks>
    public static string Normalise(string password)
    {
        // DECORATION FIRST, SUBSTITUTIONS SECOND, and the order is the whole correctness of this method.
        //
        // Substituting first turns the '0' inside a YEAR into the letter 'o', which then stops the trailing
        // strip dead: "Password2026" reduced to "password2o" and was accepted. Measured, not reasoned about —
        // four of the six cases this list exists to catch failed on exactly that, and the three that passed
        // were the ones with no zero in their decoration, which is precisely how a wrong order survives a
        // smaller test set.
        var text = password.ToLowerInvariant().Trim();

        var start = 0;
        var end = text.Length;
        while (start < end && !char.IsLetter(text[start]) && text[start] is not ('@' or '$'))
        {
            start++;
        }

        while (end > start && !char.IsLetter(text[end - 1]) && text[end - 1] is not ('@' or '$'))
        {
            end--;
        }

        var core = text[start..end];

        // Now the substitutions, over what is left: the predictable ones only. '!' is deliberately NOT read as
        // 'i' or 'l' — it is the commonest trailing decoration, where it means nothing, and treating it as a
        // letter mangles the middle of passwords that merely contain one.
        var folded = new StringBuilder(core.Length);
        foreach (var character in core)
        {
            folded.Append(character switch
            {
                '0' => 'o',
                '1' => 'l',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                '$' => 's',
                _ => character,
            });
        }

        return folded.ToString();
    }

    private static HashSet<string> Load()
    {
        // An embedded resource rather than a file beside the assembly: this has to work in the single-file
        // desktop packaging and inside the container image, neither of which has a content directory.
        var assembly = typeof(PasswordPolicy).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("common-passwords.txt", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                set.Add(line.ToLowerInvariant());
            }
        }

        return set;
    }
}
