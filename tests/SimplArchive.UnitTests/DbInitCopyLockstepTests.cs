namespace SimplArchive.UnitTests;

// EVERY copy of db-init.sql must be byte-identical to scripts/, and the chart must wire the runtime static
// role end to end (#1249, ADR 0721).
//
// WHAT WENT WRONG, AND WHY NOTHING FAILED. A Helm chart cannot read outside itself, so charts/…/files/ carries
// a copy of the bootstrap SQL. Nothing compared them. ADR 0721 added the static `simplarchive_runtime` login to
// scripts/db-init.sql — the role whose PASSWORD OpenBao rotates under a fixed username — and the chart copy
// never got it. Measured: 6 mentions on one side, 0 on the other, and the files had drifted in BOTH directions.
//
// The omission was not alone, and that is what made it invisible: db-init did not create the role,
// allowed_roles did not permit it, the policy did not grant reading it, and the deployment did not set
// OpenBao__DatabaseRuntimeStaticRole. Four gaps that AGREED with each other, so nothing errored — the app
// simply took the dynamic credential instead, which is the model 0721 exists to replace. Verified on a real
// chart install: the running database had simplarchive, simplarchive_app and simplarchive_vault, and no
// simplarchive_runtime.
//
// So this guards the whole chain rather than the file. A test that only compared the two SQL files would have
// passed the moment somebody copied one over the other, while the policy and the env stayed missing and the app
// carried on using a credential that dies at 24h.
//
// AND THERE WERE THREE COPIES, NOT TWO — which this guard missed for the same reason it was written. The KIOSK
// carries its own (tools/kiosk/config/db-init.sql), and while the chart was being fixed it sat at 115 lines
// with ZERO mentions of simplarchive_runtime. A guard that names its subjects one by one only ever watches the
// ones somebody thought of, so the list is now DERIVED: every db-init.sql in the repository is compared, and a
// fourth copy appearing anywhere is picked up without anybody remembering to add it.
//
// The kiosk drift was also the more dangerous direction. The HOST had the correct file — somebody fixed the
// running demo by hand — so the repository was no longer the source of truth, and a kiosk rebuilt from
// tools/kiosk/ would have provisioned the pre-0721 role model whose credential dies at 24h. That is issue #668
// (the kiosk's config drifts silently) recurring on the very file its item 4 already named.
public class DbInitCopyLockstepTests
{
    [Fact]
    public void Every_copy_of_db_init_is_byte_identical()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var source = Path.Combine(root, "scripts", "db-init.sql");
        var canonical = File.ReadAllText(source);

        // FOUND, not listed. Naming the copies is what let the kiosk's sit unwatched while the chart's was
        // being fixed; a search finds the one nobody remembered, and finds the next one for free.
        var copies = Directory.EnumerateFiles(root, "db-init.sql", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !string.Equals(f, source, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // Both known copies must still be among them: a rename or a move would otherwise leave this passing
        // while watching nothing, which is the failure mode the whole file is about.
        //
        // The KIOSK copy is required only in the private repository, and that is not a softening: `tools/` is
        // withheld from the public mirror (ADR 0484), so there the file is absent BY DESIGN — demanding it
        // turned the mirror's fast tier red for a day on a checkout where nothing had drifted. The comparison
        // itself still runs there over whatever copies the mirror DOES publish, which is the chart's, so the
        // published pair stays watched rather than the whole guard standing down. Asked of the ORIGIN, never of
        // the file's existence: the latter would also excuse a copy that somebody moved or deleted here.
        var required = PrivateRepositoryGate.IsPrivateRepository(root)
            ? new[]
            {
                Path.Combine("charts", "simplarchive", "files", "db-init.sql"),
                Path.Combine("tools", "kiosk", "config", "db-init.sql"),
            }
            : [Path.Combine("charts", "simplarchive", "files", "db-init.sql")];

        foreach (var name in required)
        {
            Assert.True(copies.Any(c => c.EndsWith(name, StringComparison.Ordinal)),
                $"{name.Replace(Path.DirectorySeparatorChar, '/')} is gone. If it MOVED, this guard is now "
                + "watching one fewer bootstrap script than the repository ships — which is exactly how the "
                + "chart lost a role for a year (#1249) and how the kiosk's copy went stale (#668).");
        }

        var diverged = copies
            .Where(c => File.ReadAllText(c) != canonical)
            .Select(c => "  " + Path.GetRelativePath(root, c).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Assert.True(diverged.Count == 0,
            "These copies of db-init.sql have diverged from scripts/db-init.sql:\n"
            + string.Join("\n", diverged)
            + "\n\nCopy the canonical file over them; do not edit them separately. Bootstrap scripts that "
            + "differ mean compose, Kubernetes and the kiosk provision DIFFERENT databases, and only one of "
            + "them is exercised by the test suite — which is how the chart lost an entire role for a year "
            + "(#1249), and how the kiosk's copy came to be missing simplarchive_runtime while the running "
            + "host had it (#668).");
    }

    // The four places the runtime static role has to appear. Named individually rather than counted, because a
    // total would let one come back while another went missing — and it is precisely their AGREEING that made
    // the original omission silent.
    [Theory]
    [InlineData("charts/simplarchive/files/db-init.sql", "simplarchive_runtime",
        "db-init must CREATE the role; OpenBao can only rotate the password of a role that exists")]
    [InlineData("charts/simplarchive/files/openbao-entrypoint.sh", "allowed_roles=simplarchive,simplarchive-owner,simplarchive-runtime",
        "the database engine's allowlist must name it, or the engine refuses the static role — silently, as far as the app is concerned")]
    [InlineData("charts/simplarchive/files/openbao-entrypoint.sh", "database/static-roles/simplarchive-runtime",
        "the static role itself must be registered, with a rotation statement")]
    [InlineData("charts/simplarchive/files/openbao-entrypoint.sh", "database/static-creds/simplarchive-runtime",
        "the AppRole policy must grant reading it; CLAUDE.md flags this omission as failing silently into the old path")]
    [InlineData("charts/simplarchive/templates/_helpers.tpl", "OpenBao__DatabaseRuntimeStaticRole",
        "Program.cs gates the static-password provider on this, so while it is unset the app uses the DYNAMIC credential")]
    public void The_chart_wires_the_runtime_static_role(string relativePath, string needle, string why)
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{relativePath} has moved, so this guard stopped watching it.");

        Assert.Contains(needle, File.ReadAllText(path), StringComparison.Ordinal);

        // The message belongs to the failure, not to a passing run — restated here so a future reader who hits
        // this sees WHY the line matters rather than just that a string vanished.
        Assert.True(File.ReadAllText(path).Contains(needle, StringComparison.Ordinal),
            $"{relativePath} no longer contains '{needle}'.\n\n{why}.\n\n"
            + "All four of these must be present together: they agreed with each other when all four were "
            + "absent, which is exactly why a chart install ran the wrong credential model for a year without "
            + "a single error (#1249).");
    }
}
