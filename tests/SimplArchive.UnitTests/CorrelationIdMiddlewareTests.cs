using Microsoft.AspNetCore.Http;
using SimplArchive.Api.Logging;

namespace SimplArchive.UnitTests;

// ADR "Enterprise-grade structured logging with Serilog": CorrelationIdMiddleware stamps every request with a
// correlation id on the response — a fresh one when the caller supplies none, the caller's own when they do.
public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Generates_a_correlation_id_when_none_is_supplied()
    {
        var context = new DefaultHttpContext();
        var nextCalled = false;
        var middleware = new CorrelationIdMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        var id = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.False(string.IsNullOrWhiteSpace(id));
    }

    [Fact]
    public async Task Propagates_a_caller_supplied_correlation_id()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "caller-abc-123";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("caller-abc-123", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
    }
}
