using SimplArchive.Cli;

namespace SimplArchive.UnitTests;

// saconsole's STDOUT carries shell exports, so nothing else may ever appear there.
//
// `saconsole login` is meant to be used as `eval "$(saconsole login --url …)"`, which means stdout is not a
// place for messages — it is a place for statements the shell will EXECUTE. An error written there is an
// error that gets run.
//
// That is not hypothetical. Running the packaged tool against an unreachable installation printed
// `Unexpected SocketException: Connection refused` on STDOUT, where eval would have executed it — and the
// same handler wrote every other failure there too. It was found by running the binary and reading the two
// streams separately; every build and every test until then was green.
public class SaConsoleStreamTests
{
    [Theory]
    // A wrong command line — Spectre's own parse failure.
    [InlineData("no-such-command")]
    // A missing installation — the settings validation path.
    [InlineData("whoami")]
    // An unreachable installation — the one that actually regressed.
    [InlineData("login", "--url", "http://127.0.0.1:1")]
    public void No_failure_path_writes_anything_to_stdout(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (outBefore, errBefore) = (Console.Out, Console.Error);
        var url = Environment.GetEnvironmentVariable("SACONSOLE_URL");
        var token = Environment.GetEnvironmentVariable("SACONSOLE_TOKEN");

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            // Cleared so the "missing installation" case cannot be satisfied by the developer's own shell.
            Environment.SetEnvironmentVariable("SACONSOLE_URL", null);
            Environment.SetEnvironmentVariable("SACONSOLE_TOKEN", null);

            var exit = SaConsoleApp.Build().Run(args);
            Assert.True(exit != 0, "these arguments are all supposed to fail");
        }
        finally
        {
            Console.SetOut(outBefore);
            Console.SetError(errBefore);
            Environment.SetEnvironmentVariable("SACONSOLE_URL", url);
            Environment.SetEnvironmentVariable("SACONSOLE_TOKEN", token);
        }

        Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()),
            "saconsole wrote to STDOUT on a failure path. STDOUT carries shell exports for "
            + "eval \"$(saconsole login …)\", so anything written there is executed by the shell:\n"
            + stdout);

        // And the reader must still be told something — a silent failure would satisfy the assertion above
        // while being worse than the bug it guards against.
        Assert.False(string.IsNullOrWhiteSpace(stderr.ToString()),
            "saconsole failed without writing anything to STDERR, so the reader is told nothing.");
    }
}
