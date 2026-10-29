using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cartesia;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cartesia;

public sealed class CartesiaProviderSpeechTests
{
    [Theory]
    [InlineData("tts/sonic-3.6/voice-id", true, false, false)]
    [InlineData("tts/sonic-latest/voice-id", true, false, false)]
    [InlineData("transcription/realtime/auto/ink-2", false, true, false)]
    [InlineData("agents/agent-id", false, false, true)]
    public async Task Realtime_tokens_grant_only_the_selected_capability(string model, bool tts, bool stt, bool agent)
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/access-token", request.RequestUri!.AbsolutePath);
            Assert.Equal("2026-08-14", request.Headers.GetValues("Cartesia-Version").Single());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var grants = body.RootElement.GetProperty("grants");
            Assert.Equal(tts, grants.GetProperty("tts").GetBoolean());
            Assert.Equal(stt, grants.GetProperty("stt").GetBoolean());
            Assert.Equal(agent, grants.GetProperty("agent").GetBoolean());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"ephemeral-fixture\"}") };
        });
        var response = await provider.GetRealtimeToken(new RealtimeRequest { Model = model });
        Assert.Equal("ephemeral-fixture", response.Value);
        Assert.True(response.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("2026-08-14", response.ProviderMetadata!["cartesia"].GetProperty("cartesia_version").GetString());
    }

    [Theory]
    [InlineData("tts/unknown/voice-id")]
    [InlineData("tts/sonic-3.6/")]
    [InlineData("tts/sonic-3.6/voice/extra")]
    [InlineData("transcription/realtime/auto/ink-whisper")]
    public async Task Invalid_realtime_models_never_request_a_token(string model)
    {
        var provider = CreateProvider(_ => throw new Exception("Invalid model reached the provider."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(new RealtimeRequest { Model = model }));
    }

    [Fact]
    public async Task Dynamic_speech_models_keep_voice_tags_and_advertise_streaming_without_new_ids()
    {
        var provider = CreateProvider(request => new(HttpStatusCode.OK) { Content = new StringContent(
            request.RequestUri!.AbsolutePath == "/voices" ? "{\"data\":[{\"id\":\"voice-id\",\"language\":\"en\",\"gender\":\"female\"}],\"has_more\":false}" : "{\"data\":[]}") });
        var speech = (await provider.ListModels(default)).Where(model => model.Type == "speech").ToArray();
        Assert.Equal(4, speech.Length);
        Assert.All(speech, model => { Assert.Contains("real-time", model.Tags!); Assert.Contains("realtime-token", model.Tags!); Assert.Contains("voice", model.Tags!); Assert.Contains("female", model.Tags!); });
        Assert.Contains(speech, model => model.Id == "cartesia/tts/sonic-3.6/voice-id");
    }

    [Theory]
    [InlineData("pcm", "raw", "audio/pcm")]
    [InlineData("ogg", "wav", "audio/wav")]
    public async Task SpeechRequest_reports_the_format_sent_to_Cartesia(
        string outputFormat,
        string expectedContainer,
        string contentType)
    {
        JsonElement? capturedPayload = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/tts/bytes", request.RequestUri?.AbsolutePath);
            Assert.Equal("2026-08-14", request.Headers.GetValues("Cartesia-Version").Single());
            capturedPayload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new(contentType);
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts/sonic-3.6/voice-id",
            Text = "Hello from Cartesia.",
            OutputFormat = outputFormat
        });

        Assert.NotNull(capturedPayload);
        Assert.Equal(expectedContainer, capturedPayload.Value
            .GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal(expectedContainer, response.Audio.Format);
        Assert.Equal(contentType, response.Audio.MimeType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
    }

    private static CartesiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new CartesiaProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
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
