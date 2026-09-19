using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The two api instances must stay identical apart from seeding (#1245, ADR 0808).
//
// WHY THIS IS WORTH A TEST. Two instances sit behind one proxy that round-robins between them, so ANY
// difference between them is served to a user at random: one request sees a setting, the next does not, and
// the report that comes back is "it works sometimes". That is the hardest class of bug this stack can produce,
// it cannot be reproduced on demand, and nothing else in the repo would notice it — a compose file with a
// plausible-looking extra line under one service is not a compile error and not a failing endpoint.
//
// The defence is structural: both services take their configuration from the SAME YAML anchors, so they cannot
// diverge without someone deleting an anchor reference. This test guards that structure rather than comparing
// resolved values, because comparing resolved values would need Docker and could not run in the fast tier.
//
// WHAT IS DELIBERATELY ALLOWED TO DIFFER: the seed configuration (one instance seeds, config-gated, so the
// other is a no-op rather than a second seeder racing it) and, on the kiosk, one host port bound to loopback
// for on-host troubleshooting — a host port belongs to exactly one container by necessity.
public class InstanceParityTests
{
    public static TheoryData<string, string, string> ComposeFiles() => new()
    {
        { "docker-compose.yaml", "api-env", "api-build" },
        { Path.Combine("tools", "kiosk", "docker-compose.yml"), "kiosk-api-env", "kiosk-api-build" },
    };

    public static TheoryData<string> ComposeFileNames() => new()
    {
        "docker-compose.yaml",
        Path.Combine("tools", "kiosk", "docker-compose.yml"),
    };

    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void Both_instances_take_their_environment_from_the_same_anchor(string file, string envAnchor, string buildAnchor)
    {
        var text = Read(file);

        Assert.True(text.Contains($"x-{envAnchor}: &{envAnchor}", StringComparison.Ordinal),
            $"{file} no longer defines the shared environment anchor &{envAnchor}. Both api instances must take "
            + "their configuration from one place — without it they can drift, and a difference between two "
            + "instances behind one proxy is served to a user at random.");

        // SCOPED TO THE SERVICE, deliberately. Asserting that the file merely CONTAINS `environment: *anchor`
        // passes when some other service satisfies it — `db-migrate` does — so the second instance could sprout
        // settings of its own and the guard would stay green. Verified: it did, until this was scoped.
        var second = ServiceBlock(text, "api-b");
        Assert.True(second.Contains($"environment: *{envAnchor}", StringComparison.Ordinal),
            $"{file}: `api-b` must take `environment: *{envAnchor}` VERBATIM, so it cannot acquire a setting of "
            + "its own. Give it nothing the other instance does not have — a difference between the two is "
            + "served to a user at random.");
        // Only the ENV anchor is forbidden here: `api-b` legitimately merges the build and deps anchors, which
        // is the whole point of them. What it must not do is merge the environment and add to it.
        Assert.False(second.Contains($"<<: *{envAnchor}", StringComparison.Ordinal),
            $"{file}: `api-b` merges something on top of the shared anchor, which means it is no longer "
            + "identical to the other instance. Whatever it adds, a user gets only on half of their requests.");

        var seeding = ServiceBlock(text, "api");
        Assert.True(seeding.Contains($"<<: *{envAnchor}", StringComparison.Ordinal),
            $"{file}: the seeding instance must merge `<<: *{envAnchor}` and add only its seed keys.");

        Assert.Equal(2, Count(text, $"<<: *{buildAnchor}\n    restart: unless-stopped"));
    }

    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void Only_the_seeding_instance_carries_seed_configuration(string file, string envAnchor, string _)
    {
        var text = Read(file);

        // The seed keys must appear exactly once each — under the one instance that merges the anchor. A second
        // occurrence means both instances seed, which is the race the config gate exists to prevent.
        foreach (var key in new[] { "Demo__Tenant__Name", "Demo__Administrator__Password" })
        {
            Assert.Equal(1, Count(text, key));
        }

        // …and the shared anchor must NOT carry them, or both instances would seed through the anchor itself.
        // Bounded by `services:` — the anchors all sit above it. Without a bound this ran to end-of-file and
        // swallowed the seed block it was meant to exclude, so the assertion could never fail.
        var anchorBody = Between(text, $"x-{envAnchor}: &{envAnchor}", "\nservices:");
        Assert.DoesNotContain("Demo__Tenant__Name", anchorBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ComposeFileNames))]
    public void Neither_instance_applies_migrations_at_startup(string file)
    {
        var text = Read(file);

        // Two instances migrating at once race each other — the reason the one-shot exists. The setting lives in
        // the shared anchor, so asserting it is false there covers both.
        Assert.True(text.Contains("App__ApplyMigrationsAtStartup: \"false\"", StringComparison.Ordinal),
            $"{file}: startup auto-migration must be OFF on the instances. The `db-migrate` one-shot applies "
            + "migrations once before either starts; two instances doing it concurrently is a race the "
            + "production-readiness validator already refuses in production.");
        Assert.DoesNotContain("App__ApplyMigrationsAtStartup: \"true\"", text, StringComparison.Ordinal);
        Assert.Contains("command: [\"--migrate\"]", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ComposeFileNames))]
    public void The_connection_pool_is_sized_for_more_than_one_instance(string file)
    {
        var text = Read(file);

        // The ceiling is PER PROCESS, so the database sees it multiplied by the instance count. The application
        // default is 40, which two instances turn into 80 against a stock max_connections of 100 — and a
        // PostgreSQL server that runs out answers 53300 and 500s rather than queueing (#750 took the public demo
        // down for eight minutes on exactly this arithmetic, one instance earlier).
        var match = Regex.Match(text, @"Database__MaxPoolSize: ""(\d+)""");
        Assert.True(match.Success,
            $"{file}: Database__MaxPoolSize must be set explicitly now that there are two instances — the "
            + "application default of 40 becomes 80 against the database, which is not what anyone chose.");

        var perInstance = int.Parse(match.Groups[1].Value);
        Assert.True(perInstance * 2 <= 70,
            $"{file}: {perInstance} per instance is {perInstance * 2} connections across two, which leaves too "
            + "little of a stock max_connections=100 (97 usable) for postfix, pgAdmin, OpenBao and the "
            + "migration step.");
    }

    [Theory]
    [MemberData(nameof(ComposeFileNames))]
    public void A_locally_installed_module_reaches_every_instance_through_the_shared_volume(string file)
    {
        var text = Read(file);

        // The override hazard this replaced: mounting a module onto the `api` service only gives one instance
        // the module and the other not, behind a proxy that round-robins — roughly half of requests answering
        // 404 MODULE_NOT_ACTIVE, which reads as a module bug rather than a topology one.
        Assert.True(text.Contains("modules-local:", StringComparison.Ordinal),
            $"{file}: the `modules-local` placeholder must exist. It is the ONE service a host-side module "
            + "override replaces, so every instance gets the module by construction — including instances that "
            + "do not exist yet.");
        Assert.Contains("modules-data:/app/Modules", text, StringComparison.Ordinal);
    }

    // One service's block: from its key to the next key at the same indent. Compose keys under `services:` are
    // two-space indented, so a four-space line is inside the block and a two-space line starts the next one.
    private static string ServiceBlock(string text, string name)
    {
        var marker = $"\n  {name}:\n";
        var from = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(from >= 0, $"no `{name}:` service found — the instance was renamed or removed.");
        from += marker.Length;

        var lines = text[from..].Split('\n');
        var block = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != ' ')
            {
                break;
            }

            block.Add(line);
        }

        return string.Join('\n', block);
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"anchor '{start}' not found");
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..to];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
