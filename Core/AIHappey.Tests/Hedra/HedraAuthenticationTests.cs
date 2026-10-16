using AIHappey.HeaderAuth;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Hedra;

public sealed class HedraAuthenticationTests
{
    [Fact]
    public void Header_credentials_are_available_under_the_hedra_identifier()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Hedra-Key"] = "test-credential";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(
            new HttpContextAccessor { HttpContext = context }));

        Assert.Equal("test-credential", resolver.Resolve("hedra"));
    }
}
