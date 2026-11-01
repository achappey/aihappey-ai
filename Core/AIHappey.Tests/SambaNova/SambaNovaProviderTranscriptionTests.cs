using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SambaNova;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SambaNova;

public sealed class SambaNovaProviderTranscriptionTests
{
    [Fact]
    public async Task Transcription_preserves_provider_options_and_forwards_unknown_multipart_fields()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? audioFileName = null;
        string? model = null;
        string? endpoint = null;
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            endpoint = request.RequestUri?.AbsolutePath;
            foreach (var part in Assert.IsType<MultipartFormDataContent>(request.Content))
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                var value = part.ReadAsStringAsync().GetAwaiter().GetResult();
                if (name == "file")
                    audioFileName = part.Headers.ContentDisposition.FileName?.Trim('"');
                if (name == "model")
                    model = value;

                if (!fields.TryGetValue(name, out var values))
                    fields[name] = values = [];
                values.Add(value);
            }

            return JsonResponse("""{"text":"recognized words","language":"en"}""");
        });
        var provider = CreateProvider(handler);
        var metadata = ParseElement(
            """
            {
              "language": "nl",
              "prompt": "Keep punctuation.",
              "model": "must-not-override-model",
              "file": "must-not-override-audio",
              "response_format": "verbose_json",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "whisper/translate",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["sambanova"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("sambanova")));

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("/v1/audio/translations", endpoint);
        Assert.Equal("whisper/translate", model);
        Assert.Equal("audio.wav", audioFileName);
        Assert.Equal("recognized words", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal("nl", Assert.Single(fields["language"]));
        Assert.Equal("Keep punctuation.", Assert.Single(fields["prompt"]));
        Assert.Equal("verbose_json", Assert.Single(fields["response_format"]));
        AssertJsonEqual(
            """{"nested":[{"value":1},[true,null,"text"]]}""",
            Assert.Single(fields["future_object"]));
        AssertJsonEqual("""[1,{"more":[false,2.5]},[]]""", Assert.Single(fields["future_array"]));
        Assert.Equal("unchanged", Assert.Single(fields["future_string"]));
        Assert.Equal("12345678901234567890.123456789", Assert.Single(fields["future_number"]));
        Assert.Equal("false", Assert.Single(fields["future_boolean"]));
        Assert.Equal("null", Assert.Single(fields["future_null"]));
        Assert.Equal(
            new[]
            {
                "file", "future_array", "future_boolean", "future_null", "future_number",
                "future_object", "future_string", "language", "model", "prompt", "response_format"
            },
            fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("whisper/translate", Assert.Single(fields["model"]));
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
    }

    [Fact]
    public async Task Transcription_omits_empty_known_options_and_preserves_metadata_validation()
    {
        var handler = new StaticResponseHttpMessageHandler(_ => JsonResponse("""{"text":"Transcript"}"""));
        var provider = CreateProvider(handler);
        var request = CreateRequest(ParseElement("""{"language":" ","prompt":null}"""));

        await provider.TranscriptionRequest(request);

        Assert.Equal(new[] { "file", "model", "response_format" },
            Assert.IsType<Dictionary<string, List<string>>>(handler.Fields).Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("json", Assert.Single(handler.Fields["response_format"]));

        var invalidKnownValue = CreateProvider(new StaticResponseHttpMessageHandler(_ =>
            throw new InvalidOperationException("Request should not be sent.")));
        await Assert.ThrowsAsync<JsonException>(() => invalidKnownValue.TranscriptionRequest(
            CreateRequest(ParseElement("""{"language":42}"""))));

        var invalidMetadata = CreateProvider(new StaticResponseHttpMessageHandler(_ =>
            throw new InvalidOperationException("Request should not be sent.")));
        await Assert.ThrowsAsync<JsonException>(() => invalidMetadata.TranscriptionRequest(
            CreateRequest(ParseElement("""[]"""))));
    }

    [Fact]
    public async Task Transcription_preserves_upstream_error_behavior()
    {
        var provider = CreateProvider(new StaticResponseHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("invalid audio", Encoding.UTF8, "text/plain")
            }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscriptionRequest(CreateRequest()));

        Assert.Equal("SambaNova STT failed (400): invalid audio", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement? metadata = null)
    {
        var request = new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav"
        };

        if (metadata is JsonElement options)
            request.ProviderOptions = new Dictionary<string, JsonElement> { ["sambanova"] = options };

        return request;
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement));
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static SambaNovaProvider CreateProvider(StaticResponseHttpMessageHandler handler)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(handler)));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Dictionary<string, List<string>> Fields { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    var value = await part.ReadAsStringAsync(cancellationToken);
                    if (!Fields.TryGetValue(name, out var values))
                        Fields[name] = values = [];
                    values.Add(value);
                }
            }

            return responder(request);
        }
    }
}
