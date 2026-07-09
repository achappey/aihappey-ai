using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Async;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Async;

public sealed class AsyncProviderSpeechAndTranscriptionTests
{
    [Fact]
    public async Task SpeechRequest_uses_shortcut_voice_and_base_model_id()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-binary"))
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return response;
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "async_flash_v1.0/voice-shortcut",
            Voice = "explicit-voice",
            Text = "hello world"
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/text_to_speech", capturedRequest!.RequestUri?.AbsolutePath);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("async_flash_v1.0", doc.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("voice-shortcut", doc.RootElement.GetProperty("voice").GetProperty("id").GetString());

        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Contains(response.Warnings, warning => JsonSerializer.Serialize(warning).Contains("voice is derived from model id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListModels_filters_voices_by_each_speech_model_and_ignores_failed_models()
    {
        var requestedModelIds = new List<string>();
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("/voices", request.RequestUri?.AbsolutePath);

            var body = await request.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var modelId = doc.RootElement.GetProperty("model_id").GetString()!;
            requestedModelIds.Add(modelId);

            if (modelId == "async_flash_v1.5")
                return JsonResponse("{\"detail\":{\"message\":\"not supported\"}}", HttpStatusCode.BadRequest);

            return JsonResponse(
                $$"""
                {
                  "voices": [
                    {
                      "voice_id": "{{modelId}}-voice",
                      "voice_type": "PREDEFINED",
                      "name": "Voice for {{modelId}}",
                      "description": "Test voice",
                      "language": "en,fr",
                      "gender": "Female",
                      "accent": "US",
                      "style": "calm",
                      "created_at": "2025-03-30T11:15:32Z",
                      "updated_at": "2025-03-30T11:15:32Z"
                    }
                  ],
                  "next_cursor": ""
                }
                """);
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains("async_pro_v1.0", requestedModelIds);
        Assert.Contains("async_flash_v1.5", requestedModelIds);
        Assert.Contains("async_flash_v1.0", requestedModelIds);
        Assert.Contains(models, m => m.Id == "async/async_pro_v1.0/async_pro_v1.0-voice" && m.Type == "speech");
        Assert.Contains(models, m => m.Id == "async/async_flash_v1.0/async_flash_v1.0-voice" && m.Type == "speech");
        Assert.DoesNotContain(models, m => m.Id == "async/async_flash_v1.5/async_flash_v1.5-voice");
        Assert.Contains(models, m => m.Id == "async/async_asr_v1.0" && m.Type == "transcription");
    }

    [Fact]
    public async Task TranscriptionRequest_posts_multipart_payload_and_raw_metadata_passthrough()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();

            return JsonResponse(
                """
                {
                  "text":"hello there"
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "async/async_asr_v1.0",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["async"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en",
                    custom_flag = true,
                    temperature = 0.2,
                    model_id = "should-not-override"
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal("/speech_to_text", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.StartsWith("multipart/form-data", capturedRequest.Content!.Headers.ContentType?.MediaType);

        var body = capturedBody!;
        Assert.Contains("name=model_id", body, StringComparison.Ordinal);
        Assert.Contains("async_asr_v1.0", body, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-override", body, StringComparison.Ordinal);
        Assert.Contains("name=language", body, StringComparison.Ordinal);
        Assert.Contains("en", body, StringComparison.Ordinal);
        Assert.Contains("name=custom_flag", body, StringComparison.Ordinal);
        Assert.Contains("true", body, StringComparison.Ordinal);
        Assert.Contains("name=temperature", body, StringComparison.Ordinal);
        Assert.Contains("0.2", body, StringComparison.Ordinal);

        Assert.Equal("hello there", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal("async/async_asr_v1.0", response.Response.ModelId);
    }

    private static AsyncProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));

        return new AsyncProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(httpClient));
    }

    private static AsyncProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "async" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
