using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Errors;

namespace SimplArchive.UnitTests;

/// <summary>
/// A caller that walks away is not a server fault (#1142).
/// <para>
/// On the kiosk, a scan whose separator-page detection queued behind the OCR sidecar outlived the client's
/// own <c>HttpClient.Timeout</c> — the .NET default of 100 s, which neither client overrides. The browser
/// aborted, every pending await threw <see cref="OperationCanceledException"/>, and the handler turned that
/// into <c>INTERNAL_ERROR</c> + <c>LogError</c>: an Error an administrator is asked to investigate, about a
/// request that nobody was waiting for any more.
/// </para>
/// <para>
/// The second test is the one that matters most. It would be easy to "fix" the first by treating every
/// cancellation as a client abort, and that would hide genuine server-side timeouts — the exact class of bug
/// this whole investigation started from. The distinction is whether <c>RequestAborted</c> actually fired.
/// </para>
/// </summary>
public class ApiExceptionHandlerTests
{
    private static (ApiExceptionHandler Handler, DefaultHttpContext Context, MemoryStream Body) Build(bool aborted)
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Method = "POST";
        context.Request.Path = "/api/intray/scan.pdf/processed";

        if (aborted)
        {
            var source = new CancellationTokenSource();
            source.Cancel();
            context.RequestAborted = source.Token;
        }

        return (new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance), context, body);
    }

    [Fact]
    public async Task A_caller_that_gave_up_is_not_reported_as_a_server_error()
    {
        var (handler, context, body) = Build(aborted: true);

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), default);

        Assert.True(handled);
        Assert.Equal(499, context.Response.StatusCode);

        // Nothing is written, because there is no longer a socket to write it to. A Problem Details body here
        // is not merely wasted — serialising it is the work the abandoned request was supposed to stop doing.
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_cause_is_STILL_a_server_error()
    {
        // Same exception type, nobody aborted: an internal timeout, a CancelAfter, a dependency giving up.
        // That is a real fault and must keep its 500 — otherwise this fix buys a quiet log by blinding it.
        var (handler, context, body) = Build(aborted: false);

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), default);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains("INTERNAL_ERROR", ReadBody(body));
    }

    [Fact]
    public async Task An_abort_does_not_excuse_an_unrelated_exception()
    {
        // The caller left AND something genuinely broke. The break is the news, so it keeps its 500: an abort
        // is a reason to stop reporting CANCELLATION, never a reason to stop reporting bugs.
        //
        // The STATUS is the assertion, deliberately, and not the body. On an aborted connection
        // WriteAsJsonAsync falls back to RequestAborted and writes nothing — there is no socket left to write
        // to — so a body assertion here would be asserting the impossible. (Written that way first, and it
        // failed for exactly that reason.) The status is also what carries the meaning: it is what the
        // severity branch above reads to decide Error versus Debug, which is the behaviour under test.
        var (handler, context, _) = Build(aborted: true);

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("a real bug"), default);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    private static string ReadBody(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body).ReadToEnd();
    }
}
