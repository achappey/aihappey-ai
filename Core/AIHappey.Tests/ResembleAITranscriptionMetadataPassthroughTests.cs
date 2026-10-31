using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ResembleAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ResembleAITranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_forwards_provider_options_without_losing_unknown_json()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? audioFileName = null;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "file")
                        audioFileName = part.Headers.ContentDisposition.FileName?.Trim('"');

                    if (!fields.TryGetValue(name, out var values))
                        fields[name] = values = [];
                    values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
                }

                return JsonResponse("""{"item":{"uuid":"job-1"}}""");
            }

            return JsonResponse("""{"item":{"status":"completed","text":"recognized words"}}""");
        });

        await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "speech-to-text",
            Audio = JsonSerializer.SerializeToElement("data:audio/wav;base64,YXVkaW8="),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["resembleai"] = ParseElement("""
                    {
                      "query": "Transcribe product names",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null,
                      "file": "must-not-replace-audio"
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        }, CancellationToken.None);

        Assert.Equal("Transcribe product names", Assert.Single(fields["query"]));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_object"])),
            ParseElement("""{"nested":[{"value":1},[true,null,"text"]]}""")));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_array"])),
            ParseElement("""[1,{"more":[false,2.5]},[]]""")));
        Assert.Equal("unchanged", Assert.Single(fields["future_string"]));
        Assert.Equal("12345678901234567890.123456789", Assert.Single(fields["future_number"]));
        Assert.Equal("false", Assert.Single(fields["future_boolean"]));
        Assert.DoesNotContain("future_null", fields.Keys);
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
        Assert.Equal("audio", Assert.Single(fields["file"]));
        Assert.Equal("audio.wav", audioFileName);
    }

    [Fact]
    public async Task Transcription_omits_absent_options_and_keeps_known_query_validation()
    {
        var fields = new List<string>();
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                fields.AddRange(Assert.IsType<MultipartFormDataContent>(request.Content)
                    .Select(part => part.Headers.ContentDisposition!.Name!.Trim('"')));
                return JsonResponse("""{"item":{"uuid":"job-1"}}""");
            }

            return JsonResponse("""{"item":{"status":"completed","text":"recognized words"}}""");
        });

        await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "speech-to-text",
            Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
            MediaType = "audio/wav"
        }, CancellationToken.None);

        Assert.Equal(new[] { "file" }, fields);

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            new TranscriptionRequest
            {
                Model = "speech-to-text",
                Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
                MediaType = "audio/wav",
                ProviderOptions = new()
                {
                    ["resembleai"] = ParseElement("""{"query":42}""")
                }
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task Transcription_preserves_upstream_error_details()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unsupported transcription option", Encoding.UTF8, "text/plain")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
            {
                Model = "speech-to-text",
                Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
                MediaType = "audio/wav"
            },
            CancellationToken.None));

        Assert.Equal("ResembleAI STT create job failed (400): unsupported transcription option", error.Message);
    }

    private static ResembleAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
