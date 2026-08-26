using SimplArchive.LoadTest;

namespace SimplArchive.UnitTests;

// The report counts failures since steady state began, which is right for percentiles and was silently wrong
// for failures: #754's run printed "70" while its own CSV held 80, and the ten it withheld were the ones that
// mattered — one per user, all inside the first 50 seconds, i.e. failing when the target was least busy. That
// is the observation that most cleanly rules out saturation, and the report dropped it without saying so.
//
// So the number now names its window, and warm-up failures are stated rather than discarded.
public class LoadTestWarmUpFailureReportingTests
{
    [Fact]
    public void The_failure_count_names_the_window_it_counted()
    {
        // Without "in steady state" on the line, the number invites comparison with the CSV's total and reads
        // as a discrepancy — which is exactly how an hour went into reconciling 70 against 80.
        Assert.Contains("**Failed actions**: 0 in steady state", Markdown(failures: 0, warmUp: 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Warm_up_failures_are_reported_rather_than_dropped()
    {
        var markdown = Markdown(failures: 70, warmUp: 10);

        Assert.Contains("70 in steady state", markdown, StringComparison.Ordinal);
        Assert.Contains("plus 10 during warm-up", markdown, StringComparison.Ordinal);
        // And the reader is told why it is a separate number, not merely that it exists.
        Assert.Contains("least busy", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_with_no_warm_up_failures_says_nothing_about_them()
    {
        // The counterpart: a note that appears on every clean run is noise, and noise is what stops the note
        // being read on the run where it matters.
        var markdown = Markdown(failures: 0, warmUp: 0);

        Assert.DoesNotContain("warm-up", markdown, StringComparison.OrdinalIgnoreCase);
    }

    private static string Markdown(int failures, int warmUp) => Report.Markdown(new ScenarioResult(
        "steady10",
        "http://target",
        Users: 10,
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(16),
        new Dictionary<string, TimeSpan> { ["search"] = TimeSpan.FromMilliseconds(400) },
        new Dictionary<string, TimeSpan> { ["search"] = TimeSpan.FromMilliseconds(420) },
        Pacing.Realistic,
        failures,
        new Dictionary<string, int>(),
        warmUp,
        UsersAffected: failures > 0 ? 10 : 0,
        [],
        GeneratorPeakCpu: 10,
        GeneratorValid: true));
}
