using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Rime;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Rime;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Rime;

public sealed class RimeProviderSpeechTests
{
    [Fact]
    public async Task Coda_uses_streaming_accept_header_time_scale_factor_and_base64_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "coda/astra",
            Text = "Hello from Rime!",
            OutputFormat = null,
            ProviderOptions = ProviderOptions(new RimeSpeechProviderMetadata
            {
                Language = "en",
                SamplingRate = 24000,
                TimeScaleFactor = 1.15f,
                SpeedAlpha = 0.5f
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/rime-tts", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("audio/mpeg", capturedRequest.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("astra", root.GetProperty("speaker").GetString());
        Assert.Equal("Hello from Rime!", root.GetProperty("text").GetString());
        Assert.Equal("coda", root.GetProperty("modelId").GetString());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.Equal(24000, root.GetProperty("samplingRate").GetInt32());
        Assert.Equal(1.15f, root.GetProperty("timeScaleFactor").GetSingle(), precision: 2);
        Assert.False(root.TryGetProperty("lang", out _));
        Assert.False(root.TryGetProperty("audioFormat", out _));
        Assert.False(root.TryGetProperty("speedAlpha", out _));

        Assert.Equal("AQID", response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
        Assert.Contains(response.Warnings, warning => warning.ToString()!.Contains("providerOptions.rime.speedAlpha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MistV3_uses_lang_pause_inline_speed_and_wav_accept_header()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("wav-audio"))
            };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "rime/mistv3/cove",
            Text = "Hello from Mist v3!",
            OutputFormat = "wav",
            ProviderOptions = ProviderOptions(new RimeSpeechProviderMetadata
            {
                TimeScaleFactor = 0.8f,
                PauseBetweenBrackets = true,
                InlineSpeedAlpha = "0.5, 3"
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("audio/wav", capturedRequest!.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("mistv3", root.GetProperty("modelId").GetString());
        Assert.Equal("cove", root.GetProperty("speaker").GetString());
        Assert.Equal("en", root.GetProperty("lang").GetString());
        Assert.Equal(0.8f, root.GetProperty("timeScaleFactor").GetSingle(), precision: 2);
        Assert.True(root.GetProperty("pauseBetweenBrackets").GetBoolean());
        Assert.Equal("0.5, 3", root.GetProperty("inlineSpeedAlpha").GetString());
        Assert.False(root.TryGetProperty("speedAlpha", out _));
        Assert.False(root.TryGetProperty("audioFormat", out _));

        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("wav-audio")), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
    }

    [Fact]
    public async Task MistV2_keeps_legacy_speed_alpha_and_uses_streaming_accept_header()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("mu-law-audio"))
            };
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "rime/mistv2/astra",
            Text = "Hello from Mist v2!",
            OutputFormat = "mulaw",
            ProviderOptions = ProviderOptions(new RimeSpeechProviderMetadata
            {
                SpeedAlpha = 1.3f,
                TimeScaleFactor = 0.7f,
                PauseBetweenBrackets = true,
                PhonemizeBetweenBrackets = true,
                NoTextNormalization = true,
                SaveOovs = true
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("audio/PCMU", capturedRequest!.Headers.Accept.ToString());

        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("mistv2", root.GetProperty("modelId").GetString());
        Assert.Equal("eng", root.GetProperty("lang").GetString());
        Assert.Equal(1.3f, root.GetProperty("speedAlpha").GetSingle(), precision: 2);
        Assert.True(root.GetProperty("pauseBetweenBrackets").GetBoolean());
        Assert.True(root.GetProperty("phonemizeBetweenBrackets").GetBoolean());
        Assert.True(root.GetProperty("noTextNormalization").GetBoolean());
        Assert.True(root.GetProperty("saveOovs").GetBoolean());
        Assert.False(root.TryGetProperty("timeScaleFactor", out _));
        Assert.False(root.TryGetProperty("audioFormat", out _));

        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("mu-law-audio")), response.Audio.Base64);
        Assert.Equal("audio/PCMU", response.Audio.MimeType);
        Assert.Equal("mulaw", response.Audio.Format);
        Assert.Contains(response.Warnings, warning => warning.ToString()!.Contains("providerOptions.rime.timeScaleFactor", StringComparison.OrdinalIgnoreCase));
    }

    private static RimeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new RimeProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static Dictionary<string, JsonElement> ProviderOptions(RimeSpeechProviderMetadata metadata)
        => new()
        {
            ["rime"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
