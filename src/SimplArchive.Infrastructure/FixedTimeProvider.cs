namespace SimplArchive.Infrastructure;

// A TimeProvider that always returns a fixed instant. Registered ONLY under the keyed "demo-clock" service (never as
// the app-wide TimeProvider), and only when the `Demo:Clock` config is a parseable date — so it freezes the demo
// seed's data timestamps + the audit recorder's event timestamps for byte-stable manual-capture screenshots
// (ADR 0510), while auth/token time keeps tracking the real clock. Production and the public kiosk leave `Demo:Clock`
// unset, so even "demo-clock" resolves to TimeProvider.System there.
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
