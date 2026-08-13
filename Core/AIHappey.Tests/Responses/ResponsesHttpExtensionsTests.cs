using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesHttpExtensionsTests
{
    [Fact]
    public async Task GetResponses_preserves_raw_provider_option_passthrough()
    {
        string? capturedBody = null;
        using var client = new HttpClient(new CapturingHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-test\",\"output\":[]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://api.openai.com/")
        };

        var providerOptions = JsonSerializer.SerializeToElement(new
        {
            container = new { id = "cntr_history" },
            service_tier = "default"
        });
        var request = new ResponseRequest
        {
            Model = "gpt-test",
            Input = "continue",
            Metadata = new Dictionary<string, object?>
            {
                ["openai"] = providerOptions
            }
        };

        await client.GetResponses(request, "openai");

        using var payload = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal(
            "cntr_history",
            payload.RootElement.GetProperty("container").GetProperty("id").GetString());
        Assert.Equal("default", payload.RootElement.GetProperty("service_tier").GetString());
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
