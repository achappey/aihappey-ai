using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.StepFun;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.StepFun;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.StepFun;

public sealed class StepFunProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequestForwardsCurrentProviderMetadataShape()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri?.AbsolutePath);
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return BinaryResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepfun/stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions(new StepFunSpeechProviderMetadata
            {
                Volume = 1.2,
                SampleRate = 48000,
                PronunciationMap = new StepFunSpeechPronunciationMap
                {
                    Tone = [" LOL/laugh out loudly ", "你好/ni3 hao3"]
                }
            })
        });

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("stepaudio-2.5-tts", root.GetProperty("model").GetString());
        Assert.Equal("zixinnansheng", root.GetProperty("voice").GetString());
        Assert.Equal(1.2, root.GetProperty("volume").GetDouble(), 3);
        Assert.Equal(48000, root.GetProperty("sample_rate").GetInt32());

        var pronunciationMap = root.GetProperty("pronunciation_map");
        Assert.Equal(JsonValueKind.Object, pronunciationMap.ValueKind);
        var tones = pronunciationMap.GetProperty("tone").EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        Assert.Equal(["LOL/laugh out loudly", "你好/ni3 hao3"], tones);
    }

    [Fact]
    public async Task SpeechRequestOmitsUnsetAndEmptyProviderMetadata()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return BinaryResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions(new StepFunSpeechProviderMetadata
            {
                PronunciationMap = new StepFunSpeechPronunciationMap
                {
                    Tone = [" "]
                }
            })
        });

        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("volume", out _));
        Assert.False(root.TryGetProperty("sample_rate", out _));
        Assert.False(root.TryGetProperty("pronunciation_map", out _));
    }

    [Theory]
    [InlineData(0.09)]
    [InlineData(2.01)]
    public async Task SpeechRequestRejectsOutOfRangeVolume(double volume)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions(new StepFunSpeechProviderMetadata { Volume = volume })
        }));
    }

    [Fact]
    public async Task SpeechRequestRejectsUnsupportedSampleRate()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "stepaudio-2.5-tts/zixinnansheng",
            Text = "hello",
            ProviderOptions = ProviderOptions(new StepFunSpeechProviderMetadata { SampleRate = 44100 })
        }));
    }

    private static StepFunProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static Dictionary<string, JsonElement> ProviderOptions(StepFunSpeechProviderMetadata metadata)
        => new()
        {
            ["stepfun"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpResponseMessage BinaryResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
            {
                Headers = { ContentType = new("audio/mpeg") }
            }
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
