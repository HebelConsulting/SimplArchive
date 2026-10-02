using System.Text.RegularExpressions;
using SimplArchive.Application.Security;

namespace SimplArchive.UnitTests;

// Every place a CHOSEN password is set runs it past the policy (#849).
//
// The issue names this failure twice (#689, #630) and the codebase has paid for it more often than that: a
// rule enforced at one set-point is silently absent at the others, and absence is the quiet direction — the
// password is simply accepted. There are three chosen set-points today and nothing stops a fourth.
//
// Counts per file rather than inspecting each site, for the reason StringEmptyRatchetTests records: a
// hand-rolled classifier of what a line "is" gets it wrong often enough to be worse than a number, and
// UsersController alone holds three assignments of which one is generated.
public class PasswordPolicyIsEnforcedTests
{
    // How many assignments in each file are to a GENERATED password, which needs no policy check. Named with
    // the reason, never merely counted — an unexplained exemption is indistinguishable from an oversight.
    private static readonly Dictionary<string, (int Generated, string Why)> Exempt = new()
    {
        ["UserPasswordsController.cs"] = (1,
            "the administrative reset mints 18 random bytes and returns them once; there is nothing for a "
            + "person to have chosen badly"),

        ["CryptoDemoSeeder.cs"] = (1,
            "a demo credential supplied by CONFIGURATION. Validating it here would throw inside seeding, and "
            + "a seed that throws takes the application down at boot — the failure a reshaped well-known mask "
            + "already caused. The shipped value is checked by the test below instead"),

        ["DemoDataSeeder.cs"] = (1,
            "same as the crypto demo seeder: a configured demo credential, checked by a test rather than at "
            + "boot"),
    };

    [Fact]
    public void Every_chosen_password_set_point_consults_the_policy()
    {
        var root = RepoPaths.RootOrNull()!;
        var offenders = new List<string>();
        var seen = 0;

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);

            // The LOGIN password only. WebDavPasswordHash and ImapPasswordHash are 18 random bytes issued by
            // the server and shown once — not chosen, not the exposure — and they do not match this.
            var assignments = Regex.Matches(source, @"\.PasswordHash\s*=\s*_?\w*[Hh]asher\.HashPassword").Count;
            if (assignments == 0)
            {
                continue;
            }

            seen += assignments;

            var checks = Regex.Matches(source, @"PasswordPolicy\.Refusal").Count;
            var name = Path.GetFileName(file);
            var generated = Exempt.TryGetValue(name, out var exemption) ? exemption.Generated : 0;

            if (assignments - generated != checks)
            {
                offenders.Add(
                    $"{name}: {assignments} password assignment(s), {generated} exempt as generated, "
                    + $"{checks} policy check(s) — expected {assignments - generated}");
            }
        }

        // ANTI-VACUOUS, and not hypothetical: this whole test passes trivially if the pattern stops matching —
        // a renamed hasher field, a refactor to a helper — and it would then report that every set-point is
        // guarded while examining none of them. Six today across four files.
        Assert.True(seen >= 6,
            $"The scan found only {seen} password assignments, fewer than have ever existed. The pattern has "
            + "stopped matching rather than the set-points having gone away. Fix the scan; do not lower this.");

        Assert.True(offenders.Count == 0,
            "A chosen password is being set without the policy, or an exempt generated one was added:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nIf the new site sets a password a PERSON chose, call PasswordPolicy.Refusal first and throw "
            + "PasswordRefusedException. If it mints a random one, add it to the exemption table here WITH THE "
            + "REASON — the table is how the next reader tells a deliberate exemption from an oversight.");
    }

    [Fact]
    public void The_shipped_demo_password_satisfies_the_policy()
    {
        // The seeders are exempt above, so nothing else would notice if the demo shipped a credential the
        // product itself refuses — which would be a small embarrassment on the public kiosk and a real one in
        // the manual, where it is printed.
        var compose = File.ReadAllText(Path.Combine(RepoPaths.RootOrNull()!, "docker-compose.yaml"));
        var demo = Regex.Match(compose, @"Demo__Administrator__Password:\s*""([^""]+)""").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(demo),
            "The demo administrator password could not be read out of docker-compose.yaml, so this assertion "
            + "is checking nothing — fix the scan rather than deleting the test.");

        Assert.Null(PasswordPolicy.Refusal(demo));
    }
}
