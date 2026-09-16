namespace SimplArchive.UnitTests;

// The chart's copy of db-init.sql must be byte-identical to scripts/, and the chart must wire the runtime
// static role end to end (#1249, ADR 0721).
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
public class DbInitCopyLockstepTests
{
    [Fact]
    public void The_charts_copy_of_db_init_is_byte_identical()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var source = Path.Combine(root, "scripts", "db-init.sql");
        var copy = Path.Combine(root, "charts", "simplarchive", "files", "db-init.sql");

        Assert.True(File.Exists(copy), $"{copy} is missing — the chart renders it into a ConfigMap.");

        Assert.True(File.ReadAllText(source) == File.ReadAllText(copy),
            "scripts/db-init.sql and charts/simplarchive/files/db-init.sql have diverged.\n\n"
            + "Copy one over the other; do not edit them separately. Two bootstrap scripts that differ mean "
            + "compose and Kubernetes provision DIFFERENT databases, and only one of them is exercised by the "
            + "test suite — which is how the chart lost an entire role for a year (#1249).");
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
