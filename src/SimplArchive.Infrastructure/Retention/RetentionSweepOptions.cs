namespace SimplArchive.Infrastructure.Retention;

// The retention sweep's schedule (binds from the "Retention" section). Configurable because a schedule the
// host cannot control turned out to be a defect in itself: the hardcoded app-start+3min first sweep fired in
// the middle of a CI test leg and silently disposed a test's overdue-at-birth document between its API setup
// and its first UI assertion — a "flaky" test that was really a fixed-clock race (it failed 4/4 on CI and
// 0/5 locally, because only CI's slower leg put the test inside the strike zone). The self-hosted test
// fixture now pushes InitialDelay out past any leg's lifetime; production keeps these defaults.
//
// BOTH fixtures do, since #744: that sentence was true of SelfHostedApp and silently untrue of E2EApiFactory,
// which runs the longest leg of all (~12 min on CI) and therefore had the widest strike zone. It resurfaced
// exactly as before — one retention test, CI only, never locally — and the second time it cost a release
// candidate's CI cycle. A guard applied to one of two callers is the shape that comes back.
public sealed class RetentionSweepOptions
{
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}
