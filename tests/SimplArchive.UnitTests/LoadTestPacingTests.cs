using SimplArchive.LoadTest;

namespace SimplArchive.UnitTests;

// Pacing decides whether a run is a load test or a flood (#705/#750).
//
// Tested because the two regimes answer different questions and a report that does not say which one produced
// it is not interpretable — and because the aggressive one exists specifically to be pointed at a live
// deployment, where "harder than I meant" is not a harmless mistake.
public class LoadTestPacingTests
{
    [Fact]
    public void Realistic_pacing_is_human_scale()
    {
        // A person reads what they opened before clicking the next thing. Seconds, not milliseconds — the first
        // kiosk run paused 2 s per ITERATION and produced ~20x a real user's traffic.
        Assert.True(Pacing.Realistic.MinMs >= 1_000, "a sub-second pause is not a person");
        Assert.True(Pacing.Realistic.MaxMs > Pacing.Realistic.MinMs, "a range, so users do not march in lockstep");
    }

    [Fact]
    public void Aggressive_pacing_is_far_harder_and_says_so()
    {
        // The report must announce this regime, because its numbers describe a system being deliberately driven
        // past what any user base would offer. Read as ordinary latency they would be alarming and wrong.
        var description = Pacing.Aggressive.Describe();

        Assert.True(Pacing.Aggressive.MaxMs < Pacing.Realistic.MinMs, "must be much harder than realistic");
        Assert.Contains("aggressive", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saturation", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_report_states_which_regime_produced_it()
    {
        // Two runs at different pacing are not comparable. Nothing else on the page would say so.
        var aggressive = Result(Pacing.Aggressive);
        var realistic = Result(Pacing.Realistic);

        Assert.Contains("**aggressive**", Report.Markdown(aggressive), StringComparison.Ordinal);
        Assert.Contains("realistic", Report.Markdown(realistic), StringComparison.Ordinal);
        Assert.DoesNotContain("**aggressive**", Report.Markdown(realistic), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pause_stays_inside_its_range()
    {
        var pacing = new Pacing(100, 300);
        var random = new Random(1);

        for (var i = 0; i < 200; i++)
        {
            var pause = pacing.Next(random).TotalMilliseconds;
            Assert.InRange(pause, 100, 300);
        }
    }

    [Fact]
    public void A_degenerate_range_does_not_produce_a_negative_pause()
    {
        // max < min would otherwise yield a negative delay, which Task.Delay rejects at runtime — a crash in the
        // middle of a long run rather than at its start. Program.cs refuses it up front; this is the belt.
        Assert.True(new Pacing(500, 100).Next(new Random(1)) >= TimeSpan.Zero);
    }

    private static ScenarioResult Result(Pacing pacing) =>
        new("steady10", "http://target", 10, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(16),
            new Dictionary<string, TimeSpan> { ["search"] = TimeSpan.FromMilliseconds(400) },
            new Dictionary<string, TimeSpan> { ["search"] = TimeSpan.FromMilliseconds(420) },
            pacing, 0, new Dictionary<string, int>(), 0, [], 10, true);
}
