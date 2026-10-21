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
            [],
            ["localhost"]);
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
            [],
            ["localhost"]);
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
            ["https://trusted.example"],
            ["localhost"]);
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
            [],
            ["localhost"]);
        var context = CreateContext(null);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_rejects_same_origin_from_untrusted_host_header()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            [],
            ["localhost"]);
        var context = CreateContext("http://attacker.example", "attacker.example");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_rejects_originless_cross_site_browser_request()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            [],
            ["localhost"]);
        var context = CreateContext(null);
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_rejects_originless_same_origin_request_with_rebinding_host()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            [],
            ["localhost"]);
        var context = CreateContext(null, "attacker.example");
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_allows_originless_same_origin_browser_request()
    {
        var nextCalled = false;
        var middleware = new LocalBrowserOriginMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            [],
            ["localhost"]);
        var context = CreateContext(null);
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    private static DefaultHttpContext CreateContext(string? origin, string host = "localhost")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host, 5000);
        if (origin is not null)
            context.Request.Headers.Origin = origin;
        return context;
    }
}
