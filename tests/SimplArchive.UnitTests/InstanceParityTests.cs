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
//
// THE KIOSK CASES ARE PRIVATE-REPOSITORY-ONLY, and this cost a red build on the public mirror to learn. `tests/`
// is published byte-for-byte while `tools/` is WITHHELD (ADR 0484), so a test living here that reads
// tools/kiosk/... compiles and runs on the mirror against a file that cannot exist there — five failures, on
// main, discovered only because a release was being cut. Every case therefore reads its file through
// KioskFileMissing(), which stands the kiosk half down where the input is absent BY DESIGN and nowhere else:
// inside this repo the file is required and a missing one still fails loudly.
public class InstanceParityTests
{
    public static TheoryData<string, string, string> ComposeFiles() => new()
    {
        { "docker-compose.yaml", "api-env", "api-build" },
        { Path.Combine("tools", "kiosk", "docker-compose.yml"), "kiosk-api-env", "kiosk-api-build" },
    };

    public static TheoryData<string> OverlayFiles() => new()
    {
        Path.Combine("tools", "kiosk", "docker-compose.encryption.yml"),
        Path.Combine("tools", "kiosk", "docker-compose.flightschool.yml"),
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
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

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
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

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
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

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
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

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
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

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

    // The rolling update must INSTALL the modules before it replaces anything (#1310).
    //
    // Every `up -d` in that script carries --no-deps — correct, and what makes the cutover one-at-a-time
    // rather than letting compose recreate everything at once. The cost is that compose starts no
    // dependencies either, so the `modules-local` one-shot never ran and a module updated on the host was
    // never deployed: the instances restarted, re-read the volume, and loaded the PREVIOUS assembly.
    //
    // Every signal said success — the host file was visibly new, the instances came up healthy, and the
    // script's own "instances agree on their module set" passed, because they did agree, on the stale build.
    // This guard exists because the next person to read those --no-deps flags will be right about why they
    // are there and wrong about what they cost.
    [Fact]
    public void The_rolling_update_installs_modules_before_replacing_an_instance()
    {
        const string file = "scripts/rolling-update.sh";
        var text = Read(file);

        // BOTH installers since #1246: modules-init resolves the pinned packages (ADR 0799), modules-local
        // overlays a genuinely local build. Each must run, and run before any instance is replaced.
        foreach (var oneshot in new[] { "install_oneshot modules-init", "install_oneshot modules-local" })
        {
            var install = text.IndexOf($"\n{oneshot}", StringComparison.Ordinal);
            Assert.True(install >= 0,
                $"{file}: `{oneshot}` is not invoked. Because every other `up -d` here passes --no-deps, "
                + "compose starts no dependencies, so a module updated on the host (or a moved pin) is never "
                + "installed into the volume the instances read — and the update reports success having "
                + "deployed nothing.");

            // Ordering is the whole property: installing AFTER the instances are replaced deploys the module
            // to containers that have already read the volume — the same silent no-op wearing a fix's clothes.
            var replace = text.IndexOf("--force-recreate --no-deps \"$svc\"", StringComparison.Ordinal);
            Assert.True(replace >= 0, $"{file}: the per-instance replace loop was not found — has it been renamed?");
            Assert.True(install < replace,
                $"{file}: `{oneshot}` must come BEFORE the instance replace loop; an instance replaced "
                + "first has already mapped the old assembly.");
        }

        // And the helper itself must still recreate with --no-deps — the invocation lines above prove the
        // CALLS exist, this proves they do the work.
        Assert.Contains("--force-recreate --no-deps \"$service\"", text, StringComparison.Ordinal);
    }

    // One service's block: from its key to the next key at the same indent. Compose keys under `services:` are
    // two-space indented, so a four-space line is inside the block and a two-space line starts the next one.
    /// <summary>
    /// The OVERLAY files — the add-ons — were outside this guard entirely, and the encryption overlay's own
    /// comment claimed otherwise ("BOTH instances get the switch (the InstanceParityTests rule)") while citing
    /// a guard that never read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The anchor assertions above cannot cover an overlay, and that is by design rather than an oversight to
    /// correct: an overlay MERGES keys on top of the base file's anchor per service, so it defines no anchor of
    /// its own and asserting one would fail every overlay that is written correctly. What an overlay must
    /// instead satisfy is the rule the anchor exists to produce — **both instances end up with the same
    /// settings** — so this asks that directly, of the keys themselves.
    /// </para>
    /// <para>
    /// Why it matters more here than in the base file: an overlay is where a per-tenant SWITCH lands
    /// (<c>Encryption__Modes__&lt;tenant&gt;</c>), and a switch present on one instance of two behind one proxy
    /// is the hardest defect this stack can produce — a visitor is served the encrypted answer or the plaintext
    /// one depending on which container took the request, and both look correct in isolation.
    /// </para>
    /// <para>
    /// <b>The seeding keys are the deliberate exception</b> (ADR 0808): seeding is config-gated and rides on
    /// <c>api</c> alone, so a <c>CryptoDemo__</c> key on both instances would be a second seeder rather than
    /// parity. An overlay with no <c>api</c> service at all — the flight school's, which configures a one-shot
    /// <c>modules-init</c> — has no instances to keep in step and is not this rule's business.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(OverlayFiles))]
    public void An_overlay_gives_BOTH_instances_every_setting_that_is_not_a_seed_key(string file)
    {
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

        var text = Read(file);
        if (!text.Contains("\n  api:\n", StringComparison.Ordinal))
        {
            return; // an overlay that configures no app instance (the flight school's modules-init)
        }

        var first = EnvironmentKeys(ServiceBlock(text, "api"));
        var shared = first.Where(e => !SeedingOnly(e.Key)).ToDictionary(e => e.Key, e => e.Value);

        Assert.True(text.Contains("\n  api-b:\n", StringComparison.Ordinal),
            $"{file} configures `api` but not `api-b`. Every setting here reaches one instance of two behind the "
            + $"proxy, so a visitor gets it or does not depending on which container answers: {string.Join(", ", shared.Keys)}");

        var second = EnvironmentKeys(ServiceBlock(text, "api-b"));

        foreach (var (key, value) in shared)
        {
            Assert.True(second.TryGetValue(key, out var other),
                $"{file} sets {key} on `api` but not on `api-b`. Both instances must carry it, or it is served "
                + "to a visitor at random. If it belongs to ONE instance on purpose, it is a seeding key and "
                + "belongs to the exception list in this test with its reason.");

            Assert.True(value == other,
                $"{file} sets {key} to '{value}' on `api` and '{other}' on `api-b`. Two instances behind one "
                + "proxy disagreeing about a setting is answered at random.");
        }
    }

    /// <summary>
    /// The SignalR hub must be routed with a STICKY policy, not round-robin (ADR 0839).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hub connection is a two-request handshake — <c>POST …/negotiate</c> hands out a <c>connectionId</c> that
    /// exists only in the process that issued it, then the WebSocket upgrade carries that id. Balanced
    /// independently across two instances, the upgrade reaches the instance that did not negotiate and gets a
    /// <c>404</c>, so the hub works about half the time. Observed on the live kiosk.
    /// </para>
    /// <para>
    /// This belongs beside the other instance-parity cases because it is the same family of defect: something
    /// true of one instance and not the other, served to a visitor at random. It is guarded rather than merely
    /// fixed because the failure is a COIN FLIP — it reads as flaky networking, which is the report most likely
    /// to be dismissed, and a silent reversion of one Caddyfile line would bring it back in exactly that form.
    /// </para>
    /// <para>
    /// The Valkey backplane does NOT satisfy this, and the assertion says so in its message: the backplane fans
    /// out messages between instances, which is a different problem from establishing a connection. That
    /// confusion is what let this sit.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_signalr_hub_is_routed_with_session_affinity()
    {
        var file = Path.Combine("tools", "kiosk", "Caddyfile");
        if (Withheld(file))
        {
            return; // the public mirror has no tools/, by design
        }

        var text = Read(file);

        var hub = text.IndexOf("handle /hubs/", StringComparison.Ordinal);
        Assert.True(hub >= 0,
            "The kiosk Caddyfile no longer routes /hubs/* separately. The SignalR handshake needs session "
            + "affinity across the two app instances (ADR 0839): without its own handler the hub falls back to "
            + "the round-robin block, and a negotiate on one instance followed by an upgrade on the other answers "
            + "404 — a hub that connects about half the time and reads as flaky networking.");

        // SCOPED TO THE HUB BLOCK, not the whole file: asserting the file merely CONTAINS a sticky policy would
        // pass if some other route had one while /hubs kept round_robin, which is the exact defect.
        var lines = text[hub..].Split('\n');

        // The handler ends at ITS OWN closing brace — a line whose only content is `}` at exactly one level of
        // indent, so a nested reverse_proxy's closing brace does not end the block early. Expressed by measuring
        // the indent rather than matching an escaped tab, because writing that escape by hand is how this method
        // first shipped as a string literal spanning two lines.
        var closing = Array.FindIndex(lines, 1, l => l.Trim() == "}" && l.TrimStart().Length + 1 == l.Length);
        Assert.True(closing > 0, "the /hubs handler is not a closed block — this guard cannot read it.");

        var block = string.Join('\n', lines[..closing]);

        Assert.True(block.Contains("lb_policy cookie", StringComparison.Ordinal),
            "The /hubs handler does not use a sticky (cookie) load-balancing policy. The Valkey backplane does "
            + "NOT cover this — it fans out messages between instances, while affinity is about a client being "
            + "able to establish a hub connection at all (ADR 0839, and Microsoft's documented requirement for "
            + $"multi-server SignalR even with a backplane). The handler reads:\n{block}");

        Assert.False(block.Contains("round_robin", StringComparison.Ordinal),
            $"The /hubs handler still names round_robin, which is what breaks the handshake. It reads:\n{block}");
    }

    /// <summary>The <c>KEY: value</c> pairs under a service block's <c>environment:</c>, values unquoted.</summary>
    private static Dictionary<string, string> EnvironmentKeys(string block)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || !line.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            var split = line.IndexOf(':', StringComparison.Ordinal);
            var key = line[..split].Trim();
            // Environment keys only: a nested mapping key ("environment", "volumes") carries no value on its line.
            var value = line[(split + 1)..].Trim().Trim('"');
            if (value.Length > 0 && key.Length > 0 && !key.Contains(' ', StringComparison.Ordinal))
            {
                found[key] = value;
            }
        }

        return found;
    }

    // Seeding is config-gated and runs on `api` alone (ADR 0808), so these belong to one instance BY DESIGN.
    private static bool SeedingOnly(string key) =>
        key.StartsWith("CryptoDemo__", StringComparison.Ordinal)
        || key.StartsWith("Demo__", StringComparison.Ordinal)
        || key.StartsWith("Interop__", StringComparison.Ordinal)
        || key.StartsWith("FlightSchoolDemo__", StringComparison.Ordinal);

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

    /// <summary>
    /// True when this case names a file the public mirror withholds and we are on the mirror. Deliberately
    /// narrow: it is false inside the private repository, so a genuinely deleted kiosk compose still fails.
    /// </summary>
    private static bool Withheld(string relative) =>
        relative.StartsWith("tools", StringComparison.Ordinal)
        && (PrivateRepositoryGate.RepoRoot() is not { } root || !PrivateRepositoryGate.IsPrivateRepository(root));

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
