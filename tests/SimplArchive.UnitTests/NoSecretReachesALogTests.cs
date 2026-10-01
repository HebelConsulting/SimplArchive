using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

/// <summary>
/// No secret VALUE reaches a log line or the console (#1503).
/// </summary>
/// <remarks>
/// <para>
/// The rule is in CLAUDE.md — never log passwords, card PINs, PUKs, management keys, tokens, client secrets,
/// TOTP secrets, webhook secrets, connection strings or presigned query strings. This is the guard, because
/// <b>a rule nothing measures is one that drifts</b>: <c>StringEmptyRatchetTests</c> exists because exactly
/// that happened to <c>string.Empty</c>, with ~82 violations written <i>after</i> the rule landed.
/// </para>
/// <para>
/// <b>It inspects ARGUMENTS, not the template</b>, and that distinction is the whole design. A message
/// template legitimately says "password" — <c>LogWarning("Failed login for user {UserId}: incorrect
/// password", user.Id)</c> is correct, required for SIEM brute-force aggregation, and its only argument is an
/// id. What may never appear is the <b>value</b>, which arrives as a structured argument. A scan that matched
/// the word anywhere in the call would flag that line, and a guard that is wrong about a correct line gets
/// suppressed rather than read.
/// </para>
/// <para>
/// <b>Carve-outs are NAMED, not counted</b> (#1503). A per-file budget is the wrong instrument: unlike
/// <c>string.Empty</c> there is no burn-down to track, and a number would let a new leak into a file that
/// already has an allowed line. There is exactly one, below, and a second one needs the conversation.
/// </para>
/// <para>
/// <b>Calibrated before it shipped.</b> Run across <c>src/</c> it finds <b>one</b> site, and that site is the
/// deliberate one. The measurement also corrected the issue's own expectation of two carve-outs: the login
/// line needs none, because inspecting arguments rather than the call already excludes it — which is the
/// design working as specified rather than a gap being tolerated.
/// </para>
/// </remarks>
public class NoSecretReachesALogTests
{
    /// <summary>
    /// The six logging methods the application standardizes on, plus every way it writes to a console.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than matched as <c>Log\w+</c>, which was the first attempt and which matched
    /// <c>session.Login(pin)</c> — a PKCS#11 call, not a log line. One false positive out of one hit is the
    /// ratio that teaches people to ignore a guard.
    /// </remarks>
    private static readonly Regex Call = new(
        @"\b(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)"
        + @"|(?:Ansi)?[Cc]onsole\s*\.\s*(?:MarkupLine(?:Interpolated)?|WriteLine|Write)"
        + @"|Console\s*\.\s*Error\s*\.\s*WriteLine)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// What a secret is called, as CAMEL-CASE COMPONENTS of an identifier rather than as substrings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Components, because neither substring nor word-boundary matching works.</b> <c>\bpin\b</c> never
    /// matches <c>cardPin</c> — there is no word boundary inside an identifier — so the guard missed the one
    /// shape it most needed to catch. Matching <c>pin</c> loosely instead hits <c>spinner</c>,
    /// <c>mapping</c> and <c>shipping</c>, which is the ratio that gets a guard suppressed. Splitting
    /// <c>cardPin</c> into <c>card</c> + <c>pin</c> and comparing whole components is exact in both
    /// directions.
    /// </para>
    /// <para>
    /// <b>"token" is deliberately absent.</b> <c>cancellationToken</c> is on half the calls in this codebase
    /// and <c>ConcurrencyToken</c> on most entities, so it would make the guard wrong far more often than
    /// right — and a secret-bearing one here is named <c>secret</c> or <c>credential</c> anyway.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> SecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "pin", "puk", "secret", "credential", "credentials",
    };

    /// <summary>Compound names that are not one camel component, so they are matched on the whole identifier.</summary>
    private static readonly string[] SecretCompounds =
        ["apikey", "managementkey", "privatekey", "connectionstring", "clientsecret"];

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>Whether any identifier in <paramref name="code"/> names a secret.</summary>
    private static bool NamesASecret(string code)
    {
        foreach (Match identifier in Identifier.Matches(code))
        {
            var name = identifier.Value;

            if (SecretCompounds.Any(c => name.Contains(c, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // Split on camel-case humps and underscores: cardPin → card, Pin.
            foreach (var part in Regex.Split(name, @"(?<=[a-z0-9])(?=[A-Z])|_"))
            {
                if (SecretWords.Contains(part))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The one place a secret is deliberately written out, with the reason.
    /// </summary>
    /// <remarks>
    /// <c>saconsole tenant create</c> prints the initial administrator password. The Api generates it once and
    /// stores nothing retrievable, so a caller who loses it must reset rather than look it up — printing it
    /// IS the hand-off, and the code says so beside the line. A second entry here is a conversation, not an
    /// edit.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>
    {
        ["src/SimplArchive.Cli/Commands/TenantCreateCommand.cs"] =
            "the bootstrap password hand-off: generated once, stored nowhere retrievable, and announced as such",
    };

    [Fact]
    public void No_logging_or_console_call_takes_a_secret_as_an_argument()
    {
        var offenders = Scan(RepositoryRoot())
            .Where(hit => !Allowed.ContainsKey(hit.File))
            .Select(hit => $"{hit.File}:{hit.Line} → {hit.Arguments}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A secret VALUE must never reach a log line or the console (CLAUDE.md). Found:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nLog an id or a name instead. If this is a deliberate hand-off like the bootstrap password, "
            + "it needs a NAMED entry in this test's Allowed list with its reason — and the conversation that "
            + "goes with it.");
    }

    [Fact]
    public void The_allowed_sites_still_exist_so_a_stale_carve_out_cannot_hide_a_leak()
    {
        // A carve-out that outlives the line it was written for silently protects whatever lands in that file
        // next. The same reason TestOnlyNativePackageTests refuses an ignore-list entry no project uses.
        var root = RepositoryRoot();
        var found = Scan(root).Select(hit => hit.File).ToHashSet(StringComparer.Ordinal);

        foreach (var (file, reason) in Allowed)
        {
            Assert.True(File.Exists(Path.Combine(root, file)), $"{file} no longer exists; drop its carve-out.");
            Assert.True(found.Contains(file),
                $"{file} is allowed to log a secret ({reason}) but no longer does. Remove the carve-out — "
                + "otherwise it protects whatever is written there next.");
        }
    }

    [Fact]
    public void The_scanner_catches_a_planted_leak_and_leaves_a_correct_line_alone()
    {
        // ANTI-VACUOUS, and it pins both halves of the design in one place. The first two lines must be
        // caught; the second two are the shapes that make a naive scanner useless.
        var planted = """
            _logger.LogInformation("Signed in {User}", user.Email, password);
            AnsiConsole.WriteLine($"pin is {cardPin}");
            _logger.LogWarning("Failed login for user {UserId}: incorrect password", user.Id);
            var login = session.Login(pin);
            """;

        var hits = Matches(planted).ToList();

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Contains("password", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Contains("cardPin", StringComparison.Ordinal));

        // The template may say "password" — what matters is that the only ARGUMENT is an id.
        Assert.DoesNotContain(hits, h => h.Contains("user.Id", StringComparison.Ordinal));

        // `Login(pin)` is a PKCS#11 call, not a log line. This is the false positive the first draft produced.
        Assert.DoesNotContain(hits, h => h.Contains("session.Login", StringComparison.Ordinal));
    }

    /// <summary>
    /// The secret never reaches a child process's ARGUMENT LIST — the shape CLAUDE.md's argv rule is about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ProcessSecretStore</c> documents exactly this: <i>"The secret goes in on STDIN, never in the
    /// argument list: process arguments are readable by any other process on the machine."</i> A documented
    /// claim with nothing checking it is the thing this issue exists to stop, so it is checked.
    /// </para>
    /// <para>
    /// <b>Statically, and that is defensible here only because the surface is one file.</b> The general argv
    /// case cannot be caught statically with precision — which is why #1503 split it off — but "does the
    /// parameter named <c>secret</c> appear inside an argument array" is answerable when every such array is
    /// a collection expression on one line in one class. The RUNTIME half of that rule lives where the code
    /// that echoes a command line lives: CAManagement's <c>ArgvRedactionTests</c> drives the tool with a known
    /// PIN and asserts the literal appears nowhere in the output. Core echoes no command line, so inventing a
    /// runtime test here would be testing a fake.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_secret_is_written_to_stdin_and_never_into_a_process_argument_list()
    {
        var path = Path.Combine(RepositoryRoot(), "src/SimplArchive.DesktopClient/Services/SecretStore.cs");
        Assert.True(File.Exists(path), $"{path} has moved; this guard needs re-pointing rather than deleting.");

        var source = File.ReadAllText(path);

        // Every `Run(tool, [..arguments..], input, out ..)` call: the arguments array may not mention the
        // secret, and `Encode(secret)` belongs in the INPUT position, which is the third argument.
        var calls = Regex.Matches(source, @"Run\(\s*""[^""]+""\s*,\s*\[(?<args>[^\]]*)\]\s*,(?<rest>[^;]*)");
        Assert.NotEmpty(calls);

        var leaks = calls
            .Where(c => Regex.IsMatch(c.Groups["args"].Value, @"\bsecret\b|Encode\s*\(", RegexOptions.IgnoreCase))
            .Select(c => c.Value.Replace("\n", " ").Trim())
            .ToList();

        Assert.True(leaks.Count == 0,
            "A secret reached a child process's argument list, where every other process on the machine can "
            + "read it. Pass it on stdin instead — the `input` parameter — as the other stores do:\n  "
            + string.Join("\n  ", leaks));

        // And the property is actually exercised somewhere: at least one store must pass a secret as input,
        // or this test would pass against a class that had stopped storing anything.
        Assert.Contains(calls, c => Regex.IsMatch(c.Groups["rest"].Value, @"Encode\s*\(\s*secret\s*\)"));
    }

    /// <summary>
    /// Removes the prose of string literals while KEEPING whatever an interpolation hole evaluates.
    /// </summary>
    /// <remarks>
    /// <b>This is the half the first draft got wrong, and it is the half that matters most.</b> Stripping
    /// every literal whole also strips <c>$"pin is {cardPin}"</c> down to nothing — so the likeliest real
    /// leak, a credential interpolated into one string and handed to a console writer, walked straight
    /// through a guard whose entire purpose was to catch it. #1503 predicted that shape would be unreachable
    /// statically; it is reachable, as long as the holes are kept and the prose around them is not. Found by
    /// the anti-vacuous test rather than by review, which is the argument for having one.
    /// </remarks>
    private static string StripLiterals(string arguments)
    {
        // An interpolated literal keeps only its holes, so the expressions inside are still inspected.
        var holesKept = Regex.Replace(
            arguments,
            "\\$@?\"(?:[^\"\\\\]|\\\\.)*\"",
            match => string.Concat(
                Regex.Matches(match.Value, "{([^{}]*)}").Select(hole => hole.Groups[1].Value + " ")));

        // A plain literal is prose only: a message TEMPLATE may legitimately say "password".
        return Regex.Replace(holesKept, "@?\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
    }

    private sealed record Hit(string File, int Line, string Arguments);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// Scans <c>src/</c> — the product. <c>tests/</c> is deliberately out of scope: it is published
    /// byte-for-byte (ADR 0484) and a fixture naming a throwaway password is not the failure this guards,
    /// which is a secret reaching a production log and from there a SIEM, where it cannot be unsent.
    /// </summary>
    private static IEnumerable<Hit> Scan(string root)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var arguments in Matches(text, out var lines))
            {
                yield return new Hit(relative, lines.Dequeue(), arguments);
            }
        }
    }

    private static IEnumerable<string> Matches(string text) => Matches(text, out _);

    /// <summary>
    /// The argument lists of logging/console calls that name something secret, with string literals removed
    /// so a template saying "password" is not a hit.
    /// </summary>
    private static IEnumerable<string> Matches(string text, out Queue<int> lines)
    {
        var found = new List<string>();
        var at = new Queue<int>();

        foreach (Match call in Call.Matches(text))
        {
            var start = call.Index + call.Length;
            var depth = 1;
            var i = start;
            while (i < text.Length && depth > 0)
            {
                depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
                i++;
            }

            var arguments = text[start..Math.Max(start, i - 1)];
            var withoutLiterals = StripLiterals(arguments);

            if (NamesASecret(withoutLiterals))
            {
                found.Add(arguments.Replace("\n", " ").Replace("\r", string.Empty).Trim());
                at.Enqueue(text[..call.Index].Count(c => c == '\n') + 1);
            }
        }

        lines = at;
        return found;
    }
}
