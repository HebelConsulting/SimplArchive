using System;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The session ended and could not be renewed, so the in-flight request will never succeed (#1251).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than the bare 401 travelling on.</b> <see cref="RenewingAuthHandler"/> already
/// does the right thing when a session dies — it clears the session, clears the stored token and raises
/// <see cref="RenewingAuthHandler.SessionEnded"/>, which shows the "you were signed out of &lt;server&gt;"
/// modal. But it also used to <i>return the 401 response</i>, and every caller runs it through
/// <c>EnsureSuccessStatusCode()</c>. So one ordinary expiry produced TWO outcomes racing each other: the
/// correct modal, and an <see cref="System.Net.Http.HttpRequestException"/> that nothing was catching.
/// </para>
/// <para>
/// The exception won often enough to matter. <c>AppExceptions</c> shows one dialog at a time, so whichever
/// arrived first suppressed the other — and the crash path was reached from an <c>async void</c> property
/// hook, which meant the user's session expiry was reported to them as a CRASH. That is the defect: not the
/// 401, which was handled correctly, but an expected event delivered as a fatal error.
/// </para>
/// <para>
/// Naming the condition fixes both halves at once. The throw makes every caller fail fast instead of parsing
/// a 401 body that is not there, and <c>AppExceptions.Report</c> recognises this type and stays silent,
/// because the session-ended modal raised by the same event already owns what the user is told.
/// </para>
/// </remarks>
public sealed class SessionEndedException : Exception
{
    public SessionEndedException(string apiRootUrl)
        : base($"The session for '{apiRootUrl}' ended and could not be renewed.") =>
        ApiRootUrl = apiRootUrl;

    /// <summary>Which server signed them out — the same detail the modal names.</summary>
    /// <remarks>
    /// Somebody moving between production, integration and a local stack needs to be told WHICH one dropped
    /// them; a message that only says "your session expired" makes them guess.
    /// </remarks>
    public string ApiRootUrl { get; }
}
