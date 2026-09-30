using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.UnitTests;

/// <summary>
/// The device-code poll must survive an answer it cannot parse.
/// </summary>
/// <remarks>
/// <para>
/// <b>It did not, and the cost was a user's approval.</b> The poll parsed the body before looking at the
/// status, so an EMPTY body threw a <c>JsonReaderException</c> out of the whole login — after the code had
/// been approved in the browser. The device code is then spent, so the only recovery is to run the command
/// again and approve a *second* code. Measured after a rolling api update: a poll that lands on an instance
/// mid-restart gets a bodiless answer back through the proxy.
/// </para>
/// <para>
/// The loop is bounded by the code's own expiry, so continuing on an unparseable answer cannot spin forever —
/// it ends when the code the user was shown expires, which is exactly the bound
/// <c>authorization_pending</c> already lives under.
/// </para>
/// </remarks>
public sealed class DeviceFlowBodyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("{\"error\": \"truncat")]
    public void An_unparseable_body_is_not_JSON(string body)
    {
        // The classification the poll turns on: none of these is an answer, so none of them may end the login.
        Assert.Null(DeviceFlow.Parsed(body));
    }

    [Theory]
    [InlineData("{\"error\":\"authorization_pending\"}")]
    [InlineData("{\"access_token\":\"abc\"}")]
    public void A_real_answer_still_parses(string body)
    {
        // The anti-vacuous half: a classifier that called everything unparseable would make the poll never
        // finish, which is worse than the crash it replaced.
        Assert.NotNull(DeviceFlow.Parsed(body));
    }

}
