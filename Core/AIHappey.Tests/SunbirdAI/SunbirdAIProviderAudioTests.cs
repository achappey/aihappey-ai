using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SunbirdAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SunbirdAI;

public sealed class SunbirdAIProviderAudioTests
{
    [Fact]
    public async Task Vercel_speech_passes_raw_options_uses_leaf_model_and_downloads_audio()
    {
        CapturedRequest? captured = null;
        var audio = Encoding.UTF8.GetBytes("wav-audio");
        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/audio/result.wav")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(audio)
                    {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav") }
                    }
                };

            captured = await CaptureAsync(request);
            return JsonResponse("""
                {"audio_url":"https://storage.example/audio/result.wav","model":"orpheus-3b-tts","language":"lug","duration_seconds":1.5}
                """);
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "orpheus-3b-tts",
            Text = "Wasuze otya?",
            Voice = "salt_lug_0001",
            Language = "lug",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["sunbirdai"] = JsonSerializer.SerializeToElement(new { response_mode = "url", custom_value = 42 })
            }
        });

        Assert.Equal("/tasks/audio/speech", captured?.Path);
        Assert.Equal("Bearer", captured?.AuthorizationScheme);
        Assert.Equal("test-api-key", captured?.AuthorizationParameter);
        using var body = JsonDocument.Parse(captured!.Body);
        Assert.Equal("orpheus-3b-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("url", body.RootElement.GetProperty("response_mode").GetString());
        Assert.Equal(42, body.RootElement.GetProperty("custom_value").GetInt32());
        Assert.Equal(audio, Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("lug", response.ProviderMetadata!["sunbirdai"].GetProperty("language").GetString());
    }

    [Fact]
    public async Task OpenAI_speech_uses_additional_properties_without_provider_wrapper()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/audio/spark.wav")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };

            captured = await CaptureAsync(request);
            return JsonResponse("""{"audio_url":"https://storage.example/audio/spark.wav","model":"spark-tts"}""");
        });

        var result = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "sunbirdai/spark-tts",
            Input = "Hello",
            Voice = "luganda_female",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["response_mode"] = JsonSerializer.SerializeToElement("url"),
                ["platform"] = JsonSerializer.SerializeToElement("modal")
            }
        });

        using var body = JsonDocument.Parse(captured!.Body);
        Assert.Equal("spark-tts", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("url", body.RootElement.GetProperty("response_mode").GetString());
        Assert.False(body.RootElement.TryGetProperty("providerMetadata", out _));
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio);
    }

    [Fact]
    public async Task Vercel_transcription_posts_audio_and_raw_provider_options()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CaptureAsync(request);
            return JsonResponse("""
                {"audio_transcription":"Gyebale ko","language":"lug","original_duration_minutes":0.5,"diarization_output":null}
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = "data:audio/wav;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["sunbirdai"] = JsonSerializer.SerializeToElement(new
                {
                    language = "lug",
                    platform = "runpod",
                    recognise_speakers = true,
                    adapter = "lug"
                })
            }
        });

        Assert.Equal("/tasks/audio/transcriptions", captured?.Path);
        Assert.Contains("name=audio", captured?.Body);
        Assert.Contains("name=platform", captured?.Body);
        Assert.Contains("runpod", captured?.Body);
        Assert.Contains("name=recognise_speakers", captured?.Body);
        Assert.Equal("Gyebale ko", response.Text);
        Assert.Equal("lug", response.Language);
        Assert.Equal(30f, response.DurationInSeconds);
    }

    [Fact]
    public async Task OpenAI_transcription_merges_modeled_and_additional_fields()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CaptureAsync(request);
            return JsonResponse("""{"audio_transcription":"hello","language":"eng"}""");
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "sunbirdai/whisper-large-v3",
            File = CreateAudioFile(),
            Language = "eng",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["platform"] = JsonSerializer.SerializeToElement("runpod"),
                ["whisper"] = JsonSerializer.SerializeToElement(true)
            }
        });

        Assert.Equal("hello", response.Text);
        Assert.Contains("name=language", captured?.Body);
        Assert.Contains("name=platform", captured?.Body);
        Assert.Contains("name=whisper", captured?.Body);
        Assert.DoesNotContain("providerMetadata", captured?.Body);
    }

    private static IFormFile CreateAudioFile()
    {
        var bytes = Encoding.UTF8.GetBytes("audio");
        return new FormFile(new MemoryStream(bytes, writable: false), 0, bytes.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };
    }

    private static SunbirdAIProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

    private static async Task<CapturedRequest> CaptureAsync(HttpRequestMessage request)
        => new(request.RequestUri?.AbsolutePath, request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync());

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(string? Path, string? AuthorizationScheme, string? AuthorizationParameter, string Body);

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await responder(request);
            response.RequestMessage = request;
            return response;
        }
    }
}
