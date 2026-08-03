namespace SimplArchive.Api.Configuration;

// A TimeProvider that always returns a fixed instant. Used ONLY to make the auto-generated user manual's
// screenshots byte-stable during capture (ADR 0510): when the `Demo:Clock` config is a parseable date, the demo
// seed + audit recorder resolve their "now" from this instead of the wall clock, so the audit / tasks / my-work
// screens don't shift run-to-run. Production and the public kiosk leave `Demo:Clock` unset and get
// TimeProvider.System (the real clock).
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
