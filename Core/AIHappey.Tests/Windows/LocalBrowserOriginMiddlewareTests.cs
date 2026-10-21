using AIHappey.Windows;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Windows;

public class LocalBrowserOriginMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_rejects_untrusted_origin_before_handling_request()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            []);
        var context = CreateContext("https://attacker.example");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_allows_same_origin_request()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            []);
        var context = CreateContext("http://localhost:5000");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_allows_configured_origin()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            ["https://trusted.example"]);
        var context = CreateContext("https://trusted.example");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_allows_requests_without_browser_origin()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            []);
        var context = CreateContext(null);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    private static DefaultHttpContext CreateContext(string? origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5000);
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        return context;
    }
}
