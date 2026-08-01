using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Friendli;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Friendli;

public sealed class FriendliProviderTranscriptionTests
{
    [Fact]
    public async Task Vercel_request_passes_raw_metadata_and_maps_raw_response()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CaptureAsync(request);
            return JsonResponse("""
                {
                  "text": "Hello, how are you?",
                  "usage": {
                    "type": "tokens",
                    "input_tokens": 20,
                    "output_tokens": 10,
                    "total_tokens": 30,
                    "input_audio_length_ms": 18000,
                    "processed_audio_length_ms": 24000,
                    "input_token_details": { "audio_tokens": 10, "text_tokens": 10 }
                  }
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "openai/whisper-large-v3",
            Audio = "data:audio/wav;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["friendli"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en",
                    temperature = 0.2,
                    chunking_strategy = new
                    {
                        type = "server_vad",
                        prefix_padding_ms = 400,
                        silence_duration_ms = 250,
                        threshold = 0.6
                    },
                    custom_option = "raw-value",
                    model = "must-not-override",
                    stream = true
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/serverless/v1/audio/transcriptions", captured.Path);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("test-api-key", captured.AuthorizationParameter);
        Assert.Contains("name=model", captured.Body);
        Assert.Contains("openai/whisper-large-v3", captured.Body);
        Assert.DoesNotContain("must-not-override", captured.Body);
        Assert.Contains("name=language", captured.Body);
        Assert.Contains("name=temperature", captured.Body);
        Assert.Contains("chunking_strategy[type]", captured.Body);
        Assert.Contains("chunking_strategy[threshold]", captured.Body);
        Assert.Contains("name=custom_option", captured.Body);
        Assert.Contains("raw-value", captured.Body);
        Assert.Contains("name=file", captured.Body);

        Assert.Equal("Hello, how are you?", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal(18f, response.DurationInSeconds);
        Assert.Equal("friendli/openai/whisper-large-v3", response.Response.ModelId);
        Assert.Equal("Hello, how are you?", response.ProviderMetadata!["friendli"].GetProperty("text").GetString());
        Assert.Equal(30, response.ProviderMetadata["friendli"].GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.NotNull(response.Request?.Body);
    }

    [Fact]
    public async Task OpenAI_request_uses_Friendli_endpoint_and_maps_token_usage()
    {
        CapturedRequest? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CaptureAsync(request);
            return JsonResponse("""
                {
                  "text": "hello",
                  "usage": {
                    "type": "tokens",
                    "input_tokens": 2,
                    "output_tokens": 1,
                    "total_tokens": 3,
                    "input_audio_length_ms": 1000,
                    "processed_audio_length_ms": 1000,
                    "input_token_details": { "audio_tokens": 2, "text_tokens": 0 }
                  }
                }
                """);
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "openai/whisper-large-v3",
            File = CreateAudioFile(),
            Language = "en",
            Temperature = 0.1f,
            ChunkingStrategy = new AudioTranscriptionServerVadChunkingStrategy
            {
                PrefixPaddingMs = 350,
                SilenceDurationMs = 220
            }
        });

        var json = Assert.IsType<OpenAITranscriptionResponse>(response);
        var usage = Assert.IsType<OpenAITranscriptionTokenUsage>(json.Usage);
        Assert.Equal("hello", json.Text);
        Assert.Equal(3, usage.TotalTokens);
        Assert.Equal(2, usage.InputTokenDetails?.AudioTokens);
        Assert.Equal("/serverless/v1/audio/transcriptions", captured?.Path);
        Assert.Contains("chunking_strategy[type]", captured?.Body);
        Assert.Contains("server_vad", captured?.Body);
    }

    [Fact]
    public async Task OpenAI_stream_maps_Friendli_delta_done_and_usage()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                data: {"type":"transcript.text.delta","delta":"The"}

                data: {"type":"transcript.text.delta","delta":" quick"}

                data: {"type":"transcript.text.done","text":"The quick","usage":{"type":"tokens","input_tokens":2,"output_tokens":2,"total_tokens":4,"input_audio_length_ms":1000,"processed_audio_length_ms":1000,"input_token_details":{"audio_tokens":2,"text_tokens":0}}}

                data: [DONE]

                """, Encoding.UTF8, "text/event-stream")
        }));

        var events = new List<IOpenAITranscriptionStreamEvent>();
        await foreach (var item in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "openai/whisper-large-v3",
                           File = CreateAudioFile()
                       }))
        {
            events.Add(item);
        }

        Assert.Collection(
            events,
            item => Assert.Equal("The", Assert.IsType<OpenAITranscriptionTextDelta>(item).Delta),
            item => Assert.Equal(" quick", Assert.IsType<OpenAITranscriptionTextDelta>(item).Delta),
            item =>
            {
                var done = Assert.IsType<OpenAITranscriptionTextDone>(item);
                Assert.Equal("The quick", done.Text);
                var usage = Assert.IsType<JsonElement>(done.Usage);
                Assert.Equal(4, usage.GetProperty("total_tokens").GetInt32());
            });
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

    private static FriendliProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

    private static async Task<CapturedRequest> CaptureAsync(HttpRequestMessage request)
        => new(
            request.Method,
            request.RequestUri?.AbsolutePath,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync());

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(
        HttpMethod Method,
        string? Path,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);

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
