using System.Net;
using System.Reflection;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// An ended session must reach the user as a SIGN-OUT, never as a crash (#1251).
//
// WHAT HAPPENED. The client had been open ~40 minutes, the session expired, and expanding a folder in the tree
// took the whole application down. The auth handling was not at fault: RenewingAuthHandler cleared the session,
// cleared the stored token and raised SessionEnded — which shows the "you were signed out of <server>" modal —
// and then RETURNED the 401. Every caller runs the response through EnsureSuccessStatusCode(), so one ordinary
// expiry produced two outcomes racing each other, and the losing one was suppressed because only one dialog
// shows at a time. The exception won, and it arrived from an `async partial void` property hook — async void by
// construction, so no call site can wrap it — landing on AppDomain.UnhandledException.
//
// So the two things asserted here are the two halves of that failure, and each is worth a test of its own
// because either one alone still leaves a crash: a handler that throws something nobody recognises, or a hook
// that cannot catch what it throws.
public class DesktopSessionEndDoesNotCrashTests
{
    [Fact]
    public void An_ended_session_is_recognised_and_not_reported_as_a_crash()
    {
        Assert.True(AppExceptions.IsSessionEnded(new SessionEndedException("https://demo.example")));
    }

    [Fact]
    public void It_is_still_recognised_after_crossing_a_task_boundary()
    {
        // An exception that travels through a Task arrives wrapped. A plain `is` test against the outer
        // exception passes in a unit test and then silently stops matching in the app, which is the worst
        // possible way for this to be wrong — the guard would look correct and crash anyway.
        Assert.True(AppExceptions.IsSessionEnded(
            new AggregateException(new SessionEndedException("https://demo.example"))));
    }

    // The anti-vacuous half, and the reason the decision is a predicate at all: a check that answered true for
    // everything would satisfy both tests above while switching the crash guard off entirely. Asserted through
    // Report would prove nothing — on a headless host its dialog work is posted to a dispatcher that never
    // runs, so "it did not throw" is not evidence.
    //
    // A 401 is in the list deliberately. It is the status this whole story is about, and it must NOT be treated
    // as an ended session on its own: the handler decides that, having already tried to renew, and a caller
    // seeing a bare 401 for some other reason still has a real error worth showing.
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(HttpRequestException))]
    public void A_genuine_failure_is_NOT_swallowed(Type type)
    {
        var exception = type == typeof(HttpRequestException)
            ? new HttpRequestException("Response status code does not indicate success: 401 (Unauthorized).",
                null, HttpStatusCode.Unauthorized)
            : (Exception)Activator.CreateInstance(type, "a genuine bug")!;

        Assert.False(AppExceptions.IsSessionEnded(exception),
            $"{type.Name} is treated as an ended session, so the #1251 check matches more than it should and "
            + "the crash guard now reports nothing at all.");
    }

    [Fact]
    public void No_change_hook_is_async_void_because_such_a_hook_cannot_be_guarded()
    {
        // THE CLASS, not the instance. Safe.Fire exists because Avalonia has no single UI-thread exception hook
        // (ADR 0275), and it guards ~146 handlers — but a generated `async partial void On…Changed` bypasses it
        // by construction. All 13 in the client were unguarded when this was written; the tree's was simply the
        // one a user happened to hit.
        var root = RepoRoot();
        if (root is null)
        {
            return;
        }

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src", "SimplArchive.DesktopClient"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (f, i, line))
                .Where(x => x.line.Contains("async partial void On", StringComparison.Ordinal)))
            .Select(x => $"  {Path.GetFileName(x.f)}:{x.i + 1}{x.line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "These change hooks are `async partial void`, so an exception inside one bypasses Safe.Fire and "
            + "reaches AppDomain.UnhandledException — which is how an expired session crashed the client "
            + "(#1251):\n" + string.Join("\n", offenders)
            + "\n\nMake the hook synchronous and hand the await to Safe.Fire:\n"
            + "  partial void OnXChanged(T v) => Services.Safe.Fire(() => OnXChangedAsync(v));");
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }
}
