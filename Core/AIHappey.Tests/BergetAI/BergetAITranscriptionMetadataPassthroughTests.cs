using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.BergetAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.BergetAI;

public sealed class BergetAITranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_maps_model_and_forwards_unknown_provider_options_as_root_form_fields()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? audioFileName = null;
        string? modelId = null;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in form)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                var value = part.ReadAsStringAsync().GetAwaiter().GetResult();
                if (name == "file")
                    audioFileName = part.Headers.ContentDisposition.FileName?.Trim('"');
                if (name == "model")
                    modelId = value;

                if (!fields.TryGetValue(name, out var values))
                    fields[name] = values = [];
                values.Add(value);
            }

            return JsonResponse("""{"text":"recognized words"}""");
        });

        var providerOptions = ParseElement("""
            {
              "model": "metadata-model",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "file": "must-not-replace-audio"
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "request-model",
            Audio = JsonSerializer.SerializeToElement("data:audio/wav;base64,YXVkaW8="),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["bergetai"] = providerOptions,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        Assert.True(JsonElement.DeepEquals(providerOptions,
            JsonSerializer.SerializeToElement(request).GetProperty("providerOptions").GetProperty("bergetai")));

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("metadata-model", modelId);
        Assert.Equal("audio.wav", audioFileName);
        Assert.Equal("bergetai/metadata-model", response.Response.ModelId);
        Assert.Equal("recognized words", response.Text);
        Assert.Equal(
            new[]
            {
                "file", "future_array", "future_boolean", "future_number", "future_object",
                "future_string", "model"
            },
            fields.Keys.Order(StringComparer.Ordinal));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_object"])),
            providerOptions.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(
            ParseElement(Assert.Single(fields["future_array"])),
            providerOptions.GetProperty("future_array")));
        Assert.Equal("unchanged", Assert.Single(fields["future_string"]));
        Assert.Equal("12345678901234567890.123456789", Assert.Single(fields["future_number"]));
        Assert.Equal("false", Assert.Single(fields["future_boolean"]));
        Assert.DoesNotContain("future_null", fields.Keys);
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
        Assert.Equal("must-not-replace-audio", providerOptions.GetProperty("file").GetString());
    }

    [Fact]
    public async Task Transcription_uses_request_model_when_metadata_model_is_null_or_missing()
    {
        foreach (var options in new JsonElement?[]
                 {
                     null,
                     ParseElement("null"),
                     ParseElement("""{"model":null}"""),
                     ParseElement("{}")
                 })
        {
            string? modelId = null;
            var fields = new List<string>();
            using var client = CreateClient(request =>
            {
                foreach (var part in Assert.IsType<MultipartFormDataContent>(request.Content))
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    fields.Add(name);
                    if (name == "model")
                        modelId = part.ReadAsStringAsync().GetAwaiter().GetResult();
                }

                return JsonResponse("""{"text":"recognized words"}""");
            });

            var request = new TranscriptionRequest
            {
                Model = "request-model",
                Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
                MediaType = "audio/wav"
            };
            if (options is JsonElement providerOptions)
                request.ProviderOptions = new() { ["bergetai"] = providerOptions };

            await CreateProvider(client).TranscriptionRequest(request);

            Assert.Equal("request-model", modelId);
            Assert.Equal(new[] { "file", "model" }, fields.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Transcription_preserves_metadata_validation_and_upstream_error_behavior()
    {
        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "request-model",
            Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
            MediaType = "audio/wav",
            ProviderOptions = new() { ["bergetai"] = ParseElement("""{"model":42}""") }
        }));

        using var invalidOptionsClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidOptionsClient).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "request-model",
            Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
            MediaType = "audio/wav",
            ProviderOptions = new() { ["bergetai"] = ParseElement("""[]""") }
        }));

        using var errorClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unsupported transcription option", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(errorClient).TranscriptionRequest(new TranscriptionRequest
            {
                Model = "request-model",
                Audio = JsonSerializer.SerializeToElement("YXVkaW8="),
                MediaType = "audio/wav"
            }));

        Assert.Equal("Berget STT failed (400): unsupported transcription option", error.Message);
    }

    private static BergetAIProvider CreateProvider(HttpClient client)
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
        public string? Resolve(string provider) => "test-api-key";
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
