using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Telnyx;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Telnyx;

public sealed class TelnyxProviderTranscriptionTests
{
    [Fact]
    public async Task Transcription_forwards_provider_options_without_losing_unknown_json()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var provider = CreateProvider(request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (!fields.TryGetValue(name, out var values))
                    fields[name] = values = [];
                values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
            }

            return JsonResponse("""{ "text": "hello world" }""");
        });

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["telnyx"] = ParseElement("""
                    {
                      "language": "en",
                      "prompt": "product names",
                      "temperature": 0.25,
                      "timestamp_granularities": ["segment", "word"],
                      "response_format": "json",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null,
                      "model": "not-the-request-model"
                    }
                    """),
                ["another-provider"] = JsonSerializer.SerializeToElement(new { secret = "not forwarded" })
            }
        });

        Assert.Equal("whisper-large-v3", Assert.Single(fields["model"]));
        Assert.Equal("en", Assert.Single(fields["language"]));
        Assert.Equal("product names", Assert.Single(fields["prompt"]));
        Assert.Equal("0.25", Assert.Single(fields["temperature"]));
        Assert.Equal(new[] { "segment", "word" }, fields["timestamp_granularities[]"]);
        Assert.Equal("verbose_json", Assert.Single(fields["response_format"]));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_object"])),
            ParseElement("""{"nested":[{"value":1},[true,null,"text"]]}""")));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_array"])),
            ParseElement("""[1,{"more":[false,2.5]},[]]""")));
        Assert.Equal("unchanged", Assert.Single(fields["future_string"]));
        Assert.Equal("12345678901234567890.123456789", Assert.Single(fields["future_number"]));
        Assert.Equal("false", Assert.Single(fields["future_boolean"]));
        Assert.Equal("null", Assert.Single(fields["future_null"]));
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
    }

    [Fact]
    public async Task Transcription_omits_empty_metadata_and_preserves_upstream_error_details()
    {
        var fieldNames = new List<string>();
        var provider = CreateProvider(request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            fieldNames.AddRange(form.Select(part => part.Headers.ContentDisposition!.Name!.Trim('"')));
            return JsonResponse("""{ "text": "hello world" }""");
        });

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["telnyx"] = ParseElement("""
                    {
                      "language": null,
                      "prompt": "",
                      "temperature": null,
                      "timestamp_granularities": [],
                      "response_format": null
                    }
                    """)
            }
        });

        Assert.Equal(new[] { "file", "model" }, fieldNames);

        var invalidProvider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() =>
            invalidProvider.TranscriptionRequest(new TranscriptionRequest
            {
                Model = "whisper-large-v3",
                Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                MediaType = "audio/wav",
                ProviderOptions = new Dictionary<string, JsonElement>
                {
                    ["telnyx"] = ParseElement("""{"temperature":"invalid"}""")
                }
            }));

        var failingProvider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unsupported option", Encoding.UTF8, "text/plain")
        });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingProvider.TranscriptionRequest(new TranscriptionRequest
            {
                Model = "whisper-large-v3",
                Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                MediaType = "audio/wav"
            }));

        Assert.Contains("Telnyx STT failed (400): unsupported option", exception.Message);
    }

    private static TelnyxProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new TelnyxProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
