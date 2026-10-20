using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;

namespace AIHappey.Tests.Http;

public sealed class ProviderCredentialIsolationTests
{
    [Fact]
    public void Generic_response_merge_cannot_reintroduce_inherited_provider_credentials()
    {
        var merge = typeof(AIHappey.Core.AI.ModelProviderResponsesUnifiedExtensions)
            .GetMethod("MergeRequestHeaders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var configured = new Dictionary<string, string> { ["X-Api-Key"] = "configured-native", ["OpenAI-Beta"] = "responses=v1" };
        var inherited = new Dictionary<string, string> { ["X-OpenAI-Key"] = "selected-SECRET", ["X-Anthropic-Key"] = "unrelated-SECRET", ["X-Request-Id"] = "correlation" };
        var result = (IReadOnlyDictionary<string, string>)merge.Invoke(null, [configured, inherited])!;
        Assert.Equal("configured-native", result["X-Api-Key"]);
        Assert.Equal("correlation", result["X-Request-Id"]);
        Assert.False(result.ContainsKey("X-OpenAI-Key"));
        Assert.False(result.ContainsKey("X-Anthropic-Key"));
        Assert.Equal(3, inherited.Count);
    }

    [Theory]
    [InlineData("X-Google-Key", "google/model", true)]
    [InlineData("x-gOoGlE-key", "google/model", true)]
    [InlineData("X-GoogleTranslate-Key", "google/model", false)]
    [InlineData("X-OpenAIHK-Key", "openai/model", false)]
    [InlineData("X-302AI-Key", "ai302/model", true)]
    [InlineData("X-Unknown-Key", null, false)]
    public void Credential_ownership_is_exact(string header, string? model, bool expected)
        => Assert.Equal(expected, ProviderCredentialHeaders.IsRequired(header, model));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_response_transport_drops_inherited_provider_keys_but_preserves_configured_AzureAuth_provider_credentials(bool streaming)
    {
        using var http = new HttpClient(new CaptureHandler(request =>
        {
            Assert.False(request.Headers.Contains("X-OpenAI-Key"));
            Assert.False(request.Headers.Contains("X-Anthropic-Key"));
            Assert.Equal("configured-provider-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("configured-native-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? "data: [DONE]\n\n" : "{\"id\":\"test\",\"model\":\"model\",\"output\":[]}", Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        })) { BaseAddress = new Uri("https://provider.test/") };
        // Both HeaderAuth resolution and AzureAuth configuration produce the same native client credentials.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "configured-provider-token");
        var options = new ResponseRequest { Model = "openai/model", Headers = new()
        {
            ["X-OpenAI-Key"] = "openai-SECRET", ["X-Anthropic-Key"] = "unrelated-SECRET"
        } };
        var explicitHeaders = new Dictionary<string, string> { ["X-Api-Key"] = "configured-native-key" };
        if (streaming)
        {
            await foreach (var part in http.GetResponsesUpdates(options, "openai", headers: explicitHeaders)) { }
        }
        else await http.GetResponses(options, "openai", headers: explicitHeaders);
        Assert.Equal(2, options.Headers.Count); // Filtering must not mutate input or shared defaults.
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
