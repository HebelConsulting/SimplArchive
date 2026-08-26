using SimplArchive.LoadTest;

namespace SimplArchive.UnitTests;

// What the load harness's verdict means (#705, ADR 0700).
//
// Written because calibration produced a report that read PASS-shaped for an action nobody measured, and FAIL
// for fifty milliseconds of noise — both plausible on the screen, both wrong. A verdict is only worth acting on
// if the rule behind it has been seen to say no.
public class LoadTestVerdictTests
{
    private static ScenarioResult Result(
        Dictionary<string, TimeSpan> baseline,
        Dictionary<string, TimeSpan> steady,
        int failures = 0,
        Dictionary<string, int>? failuresByAction = null,
        int usersAffected = 0,
        double generatorCpu = 10,
        bool generatorValid = true,
        int warmUpFailures = 0) =>
        new("steady10", "http://target", 10, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(16),
            baseline, steady, Pacing.Realistic, failures, failuresByAction ?? [], warmUpFailures, usersAffected,
            [], generatorCpu, generatorValid);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_real_degradation_fails_the_run()
    {
        var result = Result(
            new() { ["search"] = Ms(800) },
            new() { ["search"] = Ms(2400) }); // 3× on an action a person feels

        Assert.False(Report.Passed(result));
        Assert.Contains("**degraded**", Report.Markdown(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_ratio_on_a_tiny_action_does_not()
    {
        // Calibration's actual numbers: 0.04 s → 0.09 s, which is 2.01× and fifty milliseconds. Failing a run
        // on that teaches everyone to ignore the run.
        var result = Result(
            new() { ["open document"] = Ms(40) },
            new() { ["open document"] = Ms(90) });

        Assert.True(Report.Passed(result));
        Assert.Contains("under the 250 ms floor", Report.Markdown(result), StringComparison.Ordinal);
    }

    [Fact]
    public void An_action_with_no_steady_samples_is_reported_as_such_rather_than_as_zero()
    {
        // Login happens once per user, in warm-up, so steady state holds none. Reported as 0.00 s it read as
        // the FASTEST action in the table — a healthy-looking row for something nobody measured, which is worse
        // than an ugly one.
        var result = Result(new() { ["login"] = Ms(2700) }, steady: []);

        var markdown = Report.Markdown(result);
        Assert.Contains("no steady-state samples", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("0.00 s | 0.00×", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_failed_action_fails_the_run_however_fast_everything_was()
    {
        // Latency is the secondary question. An action that did not complete is a person who could not do their
        // work, and no percentile redeems it.
        var result = Result(
            new() { ["upload"] = Ms(900) },
            new() { ["upload"] = Ms(950) },
            failures: 1);

        Assert.False(Report.Passed(result));
    }

    [Fact]
    public void An_action_that_failed_never_reads_as_ok_however_good_its_percentile_is()
    {
        // The first kiosk run's actual shape: an eight-minute outage in which every request either timed out or,
        // if it got through, was fast. Timeouts are excluded from the percentile — a timeout's duration is the
        // ceiling, not a latency — so `open repository` and `search` both rendered "ok" while the site was down,
        // and only a total above the table disagreed. The failure is the more important fact about the row.
        var result = Result(
            new() { ["open repository"] = Ms(1260) },
            new() { ["open repository"] = Ms(500) }, // FASTER than baseline, and still an outage
            failures: 11,
            failuresByAction: new() { ["open repository"] = 11 },
            usersAffected: 10);

        var markdown = Report.Markdown(result);
        Assert.False(Report.Passed(result));
        Assert.Contains("**11 failed**", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| ok |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_user_failing_is_called_an_outage_rather_than_a_stuck_browser()
    {
        // One user failing repeatedly and all ten failing are different findings, and the first kiosk run could
        // not tell them apart because the samples carried no user id. When the data can answer it, the report
        // must — a reader should not have to re-derive it from a CSV.
        var outage = Result(
            new() { ["search"] = Ms(400) },
            new() { ["search"] = Ms(420) },
            failures: 12,
            failuresByAction: new() { ["search"] = 12 },
            usersAffected: 10);

        var oneStuckBrowser = Result(
            new() { ["search"] = Ms(400) },
            new() { ["search"] = Ms(420) },
            failures: 12,
            failuresByAction: new() { ["search"] = 12 },
            usersAffected: 1);

        Assert.Contains("target being unavailable", Report.Markdown(outage), StringComparison.Ordinal);
        Assert.DoesNotContain("target being unavailable", Report.Markdown(oneStuckBrowser), StringComparison.Ordinal);
        Assert.Contains("across 1 of 10 users", Report.Markdown(oneStuckBrowser), StringComparison.Ordinal);
    }

    [Fact]
    public void A_saturated_generator_makes_the_run_invalid_rather_than_passing()
    {
        // The honesty guard. Everything here looks healthy — and that is exactly the run that must not be
        // published as evidence, because a generator at its limit cannot tell you whose latency it measured.
        var result = Result(
            new() { ["search"] = Ms(800) },
            new() { ["search"] = Ms(820) },
            generatorCpu: 94,
            generatorValid: false);

        Assert.False(Report.Passed(result));

        var markdown = Report.Markdown(result);
        Assert.StartsWith("# Load test — steady10 — INVALID", markdown, StringComparison.Ordinal);
        Assert.Contains("is not evidence about the server", markdown, StringComparison.Ordinal);
    }
}
