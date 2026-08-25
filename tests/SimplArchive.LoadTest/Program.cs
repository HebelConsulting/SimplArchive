using Microsoft.Playwright;
using SimplArchive.LoadTest;
using SimplArchive.SelfHosting;

// The kiosk load harness (#705, ADR 0700). Invoked by scripts/load-test.sh; never by CI.
//
//   --target <url>        the instance to load. Omit to self-host one, which is how the harness is proved
//                         before it is ever pointed at the kiosk.
//   --scenario steady10   the only scenario in this slice; `ramp` follows in its own.
//   --users N             overrides the scenario's user count (for calibrating on a small machine).
//   --minutes M           steady-state duration after warm-up.
//   --out <dir>           where the report and CSV land (default loadtest-results/).
//   --email / --password  the account the simulated users sign in as. Defaults to the SELF-HOSTED seed's
//                         admin — which is not the kiosk's: the kiosk overrides Demo__Administrator__Email, so
//                         its address carries the deployment's own domain. A remote run that used the default
//                         would fail every login and report it as the server failing, which is the wrong
//                         finding stated confidently. scripts/load-test.sh reads the real ones out of the
//                         published README, as the other kiosk checks do.

var target = Arg("--target");
var scenario = Arg("--scenario") ?? "steady10";
var users = int.TryParse(Arg("--users"), out var u) ? u : 10;
var minutes = double.TryParse(Arg("--minutes"), out var m) ? m : 15;
var outDir = Arg("--out") ?? "loadtest-results";
var email = Arg("--email") ?? SelfHostedApp.AdminEmail;
var password = Arg("--password") ?? SelfHostedApp.AdminPassword;

if (target is not null && (Arg("--email") is null || Arg("--password") is null))
{
    Console.Error.WriteLine(
        "--target needs --email and --password: the built-in defaults are the self-hosted seed's, and a remote "
        + "deployment's admin address carries its own domain. Using them would fail every login and look like "
        + "the server refusing.");
    return 2;
}

if (scenario != "steady10")
{
    Console.Error.WriteLine($"Unknown scenario '{scenario}'. This build has: steady10.");
    return 2;
}

// The boot engine, pointed at a remote target or asked to make one. Everything downstream reads BaseUrl only.
var app = new SelfHostedApp { RemoteTarget = target };
await using var _ = app;

if (target is null)
{
    Console.WriteLine("No --target: self-hosting an instance (this is the calibration path, not a kiosk run)…");
}

await app.StartAsync();
Console.WriteLine($"Target: {app.BaseUrl}");

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    // The system Chrome, as the UI suite uses — the point is to measure what a visitor's browser experiences,
    // and a bundled build would be a different renderer with different costs.
    Channel = "chrome",
    Headless = true,
});

var log = new ActionLog();
var generator = new GeneratorLoad();
using var generatorStop = new CancellationTokenSource();
var sampling = generator.SampleAsync(generatorStop.Token);

var startedAt = DateTimeOffset.UtcNow;
var runClock = System.Diagnostics.Stopwatch.StartNew();

// ---- Baseline: ONE user, alone, right now ------------------------------------------------------------------
//
// Measured here rather than configured, so the yardstick belongs to this machine, this dataset and this build.
// A number written into a file would be wrong the first time anything changed, and wrong silently.
Console.WriteLine("Measuring single-user baseline…");
var baselineLog = new ActionLog();
await using (var baselineContext = await browser.NewContextAsync())
{
    var solo = new BrowserUser(baselineContext, app.BaseUrl, baselineLog, email, password, user: 0);
    if (await solo.LoginAsync())
    {
        for (var i = 0; i < 4; i++)
        {
            await solo.IterateAsync(i, CancellationToken.None);
        }
    }
}

var baseline = baselineLog.Actions().ToDictionary(a => a, a => baselineLog.P95(a) ?? TimeSpan.Zero);
foreach (var (action, p95) in baseline.OrderBy(kv => kv.Key))
{
    Console.WriteLine($"  baseline {action,-18} p95 {p95.TotalSeconds,6:F2} s");
}

// ---- Steady state: N users, together --------------------------------------------------------------------
Console.WriteLine($"Warming up {users} users…");
using var stop = new CancellationTokenSource();
var contexts = new List<IBrowserContext>();
var loops = new List<Task>();

for (var i = 0; i < users; i++)
{
    var context = await browser.NewContextAsync();
    contexts.Add(context);
    var user = new BrowserUser(context, app.BaseUrl, log, email, password, user: i + 1);

    loops.Add(Task.Run(async () =>
    {
        if (!await user.LoginAsync())
        {
            return; // recorded as a failed action; the run continues and the report will say so
        }

        for (var iteration = 0; !stop.IsCancellationRequested; iteration++)
        {
            try
            {
                await user.IterateAsync(iteration, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }));

    // Staggered: ten browsers starting in the same second is a thundering herd, which measures a spike rather
    // than ten people working.
    await Task.Delay(TimeSpan.FromSeconds(2));
}

// Warm-up is excluded from the verdict — first paint, WASM boot and an empty cache are one-off costs that a
// steady-state question is not asking about.
await Task.Delay(TimeSpan.FromMinutes(1));
var steadyFrom = DateTimeOffset.UtcNow;
Console.WriteLine($"Steady state for {minutes} minutes…");
await Task.Delay(TimeSpan.FromMinutes(minutes));

await stop.CancelAsync();

// Say so before waiting. A user mid-action is inside a Playwright wait budgeted at the harness's deliberately
// wide ceiling, and WhenAll waits for it — so a clean shutdown can legitimately take a couple of minutes, and
// against a degraded target it takes longer. Silence here reads as a hang, which is how the first kiosk run's
// teardown was nearly diagnosed as one.
Console.WriteLine("Stopping — draining in-flight actions (up to 2 min while their timeouts expire)…");
await Task.WhenAll(loops);
foreach (var context in contexts)
{
    await context.CloseAsync();
}

await generatorStop.CancelAsync();
await sampling;

// ---- Verdict --------------------------------------------------------------------------------------------
// Only actions that actually produced steady-state samples. A null p95 must NOT become a zero: that reads as
// a fast, healthy action in the report, for something nobody measured (calibration showed exactly that for
// "login", which by design only happens during warm-up).
var steady = log.Actions()
    .Select(a => (Action: a, P95: log.P95(a, steadyFrom)))
    .Where(x => x.P95 is not null)
    .ToDictionary(x => x.Action, x => x.P95!.Value);
var result = new ScenarioResult(
    scenario, app.BaseUrl, users, startedAt, runClock.Elapsed, baseline, steady,
    log.Failures(steadyFrom), log.FailuresByAction(steadyFrom), log.UsersAffected(steadyFrom),
    [.. log.Samples.Where(s => s.Failed && s.StartedAt >= steadyFrom).Select(s => $"{s.Action}: {s.Error}").Distinct()],
    generator.PeakPercent, generator.RunIsValid);

Directory.CreateDirectory(outDir);
var stamp = startedAt.ToString("yyyyMMdd-HHmmss");
var markdownPath = Path.Combine(outDir, $"{scenario}-{stamp}.md");
await File.WriteAllTextAsync(markdownPath, Report.Markdown(result));
await File.WriteAllTextAsync(Path.Combine(outDir, $"{scenario}-{stamp}.csv"), Report.Csv(log.Samples));

Console.WriteLine();
Console.WriteLine(Report.Markdown(result));
Console.WriteLine($"Report: {markdownPath}");

// INVALID is its own exit code: a caller must not read "not 0" as "the server failed" when the truth is that
// this machine could not answer the question.
return !result.GeneratorValid ? 3 : Report.Passed(result) ? 0 : 1;

string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
