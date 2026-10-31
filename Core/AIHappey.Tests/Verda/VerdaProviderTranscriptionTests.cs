using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Verda;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Verda;

public sealed class VerdaProviderTranscriptionTests
{
    [Fact]
    public async Task Transcription_request_preserves_provider_key_and_forwards_unknown_options()
    {
        string? capturedPayload = null;
        var provider = CreateProvider((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/whisper/predict")
            {
                capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"output":"caption"}""");
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        using var metadata = JsonDocument.Parse("""
            {
              "audio_input": " https://audio.example/input.wav ",
              "Translate": false,
              "Language": " en ",
              "processing_type": " align ",
              "output": "subtitles",
              "future_option": {"values":[1,true,null,{"nested":"kept"}]},
              "future_array": [null,2,"kept"],
              "future_flag": true,
              "future_number": 2.5,
              "future_string": "kept",
              "future_null": null
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "whisper",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["verda"] = metadata.RootElement.Clone()
            }
        };

        var clientPayload = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(clientPayload.GetProperty("providerOptions").TryGetProperty("verda", out _));
        Assert.False(clientPayload.TryGetProperty("verda", out _));

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("caption", response.Text);
        using var upstream = JsonDocument.Parse(Assert.IsType<string>(capturedPayload));
        var root = upstream.RootElement;
        Assert.Equal("https://audio.example/input.wav", root.GetProperty("audio_input").GetString());
        Assert.False(root.GetProperty("translate").GetBoolean());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal("align", root.GetProperty("processing_type").GetString());
        Assert.Equal("subtitles", root.GetProperty("output").GetString());
        Assert.Equal(1, root.GetProperty("future_option").GetProperty("values")[0].GetInt32());
        Assert.True(root.GetProperty("future_option").GetProperty("values")[1].GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_option").GetProperty("values")[2].ValueKind);
        Assert.Equal("kept", root.GetProperty("future_option").GetProperty("values")[3].GetProperty("nested").GetString());
        Assert.Equal(3, root.GetProperty("future_array").GetArrayLength());
        Assert.True(root.GetProperty("future_flag").GetBoolean());
        Assert.Equal(2.5, root.GetProperty("future_number").GetDouble());
        Assert.Equal("kept", root.GetProperty("future_string").GetString());
        Assert.False(root.TryGetProperty("future_null", out _));
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("verda", out _));
        Assert.Equal(10, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Transcription_request_keeps_default_audio_input_and_omits_optional_fields()
    {
        string? capturedPayload = null;
        var provider = CreateProvider((request, _) =>
        {
            capturedPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"text":"hello"}""");
        });
        var audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio"));

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper",
            Audio = audio,
            MediaType = "audio/wav"
        });

        using var upstream = JsonDocument.Parse(Assert.IsType<string>(capturedPayload));
        var root = upstream.RootElement;
        Assert.Equal($"data:audio/wav;base64,{audio}", root.GetProperty("audio_input").GetString());
        Assert.Equal(1, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Transcription_request_keeps_invalid_known_metadata_error_behavior()
    {
        var requestCount = 0;
        var provider = CreateProvider((_, _) =>
        {
            requestCount++;
            return Json("""{"text":"unexpected"}""");
        });

        await Assert.ThrowsAsync<JsonException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["verda"] = JsonSerializer.SerializeToElement(new { translate = "false" })
            }
        }));

        Assert.Equal(0, requestCount);
    }

    private static VerdaProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request, cancellationToken));
    }
}
