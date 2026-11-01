using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Noiz;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Noiz;

public sealed class NoizProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task SpeechRequest_preserves_known_options_and_forwards_unknown_provider_options()
    {
        using var metadata = JsonDocument.Parse("""
            {
              "voice_id": "metadata-voice",
              "quality_preset": 3,
              "output_format": "mp3",
              "speed": 0.8,
              "duration": 1.75,
              "target_lang": "de",
              "similarity_enh": false,
              "emo": "calm",
              "trim_silence": true,
              "save_voice": false,
              "text": "metadata text",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        Dictionary<string, string>? formFields = null;
        var provider = CreateProvider(message =>
        {
            Assert.Equal("/v1/text-to-speech", message.RequestUri!.AbsolutePath);
            formFields = ReadForm(message.Content!);
            return AudioResponse();
        });

        var request = new SpeechRequest
        {
            Model = "noiz/text-to-speech/model-voice",
            Text = "request text",
            Voice = "request-voice",
            OutputFormat = "wave",
            Speed = 1.1,
            Language = "es",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["noiz"] = metadata.RootElement.Clone(),
                ["another-provider"] = JsonDocument.Parse("""{"must_not_forward":true}""").RootElement.Clone()
            }
        };
        var clientContract = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(
            metadata.RootElement,
            clientContract.GetProperty("providerOptions").GetProperty("noiz")));

        await provider.SpeechRequest(request);

        Assert.NotNull(formFields);
        Assert.True(JsonElement.DeepEquals(metadata.RootElement, request.ProviderOptions!["noiz"]));
        Assert.Equal("request text", formFields["text"]);
        Assert.Equal("model-voice", formFields["voice_id"]);
        Assert.Equal("3", formFields["quality_preset"]);
        Assert.Equal("wav", formFields["output_format"]);
        Assert.Equal("1.1", formFields["speed"]);
        Assert.Equal("1.75", formFields["duration"]);
        Assert.Equal("es", formFields["target_lang"]);
        Assert.Equal("false", formFields["similarity_enh"]);
        Assert.Equal("calm", formFields["emo"]);
        Assert.Equal("true", formFields["trim_silence"]);
        Assert.Equal("false", formFields["save_voice"]);
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"nested":[{"value":1},[true,null,"text"]]}""").RootElement,
            JsonDocument.Parse(formFields["future_object"]).RootElement));
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""[1,{"more":[false,2.5]},[]]""").RootElement,
            JsonDocument.Parse(formFields["future_array"]).RootElement));
        Assert.Equal("unchanged", formFields["future_string"]);
        Assert.Equal("12345678901234567890.123456789", formFields["future_number"]);
        Assert.Equal("false", formFields["future_boolean"]);
        Assert.Equal("null", formFields["future_null"]);
        Assert.False(formFields.ContainsKey("another-provider"));
        Assert.False(formFields.ContainsKey("must_not_forward"));
        Assert.Equal(17, formFields.Count);
    }

    [Fact]
    public async Task SpeechRequest_omits_null_known_options_and_preserves_known_type_validation()
    {
        Dictionary<string, string>? fields = null;
        var provider = CreateProvider(message =>
        {
            fields = ReadForm(message.Content!);
            return AudioResponse();
        });
        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "noiz/text-to-speech",
            Text = "request text",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["noiz"] = JsonDocument.Parse("""
                    {
                      "quality_preset": null,
                      "output_format": null,
                      "speed": null,
                      "duration": null,
                      "target_lang": null,
                      "similarity_enh": null,
                      "emo": null,
                      "trim_silence": null,
                      "save_voice": null
                    }
                    """).RootElement.Clone()
            }
        });

        Assert.NotNull(fields);
        Assert.Equal(new[] { "text" }, fields.Keys);

        var invalidProvider = CreateProvider(_ => throw new InvalidOperationException("The upstream API must not be called."));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.SpeechRequest(new SpeechRequest
        {
            Model = "noiz/text-to-speech",
            Text = "request text",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["noiz"] = JsonDocument.Parse("""{"quality_preset":"invalid"}""").RootElement.Clone()
            }
        }));
    }

    [Fact]
    public async Task SpeechRequest_preserves_upstream_error_behavior()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid request")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "noiz/text-to-speech",
            Text = "request text"
        }));

        Assert.Equal("Noiz TTS failed (400): invalid request", exception.Message);
    }

    [Theory]
    [InlineData("noiz/text-to-sound", "/v1/text-to-sound", "prompt")]
    [InlineData("noiz/guest-text-to-speech", "/v1/guest/text-to-speech", "text")]
    public async Task SpeechRequest_forwards_unknown_options_for_other_endpoints(string model, string path, string inputField)
    {
        Dictionary<string, string>? fields = null;
        var provider = CreateProvider(message =>
        {
            Assert.Equal(path, message.RequestUri!.AbsolutePath);
            fields = ReadForm(message.Content!);
            return AudioResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = model,
            Text = "request text",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["noiz"] = JsonDocument.Parse("""
                    {"future_option":{"nested":[true,null,{"value":7}]}}
                    """).RootElement.Clone()
            }
        });

        Assert.NotNull(fields);
        Assert.Equal("request text", fields[inputField]);
        using var forwarded = JsonDocument.Parse(fields["future_option"]);
        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse("""{"nested":[true,null,{"value":7}]}""").RootElement,
            forwarded.RootElement));
        Assert.Equal(2, fields.Count);
    }

    private static Dictionary<string, string> ReadForm(HttpContent content)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in Assert.IsType<MultipartFormDataContent>(content))
        {
            var name = part.Headers.ContentDisposition!.Name!.Trim('"');
            fields[name] = part.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        return fields;
    }

    private static NoizProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new TestApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(new HttpClient(new TestHandler(responder))));

    private static HttpResponseMessage AudioResponse()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
