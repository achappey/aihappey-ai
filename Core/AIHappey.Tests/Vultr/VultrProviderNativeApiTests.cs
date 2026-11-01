using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Vultr;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Vultr;

public sealed class VultrProviderNativeApiTests
{
    [Fact]
    public async Task Responses_and_messages_use_vultr_native_endpoints()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider(request =>
        {
            requests.Add(request);
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/responses" => JsonResponse("""
                    {"id":"resp_1","object":"response","created_at":1730000000,"status":"completed","model":"llama-3","output":[]}
                    """),
                "/v1/messages" => JsonResponse("""
                    {"id":"msg_1","type":"message","role":"assistant","content":[],"model":"llama-3","stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                    """),
                _ => JsonResponse("{}", HttpStatusCode.NotFound)
            };
        });

        await provider.ResponsesAsync(new ResponseRequest { Model = "llama-3" });
        var message = await provider.MessagesAsync(
            new MessagesRequest { Model = "llama-3", MaxTokens = 16, Messages = [] },
            []);

        Assert.Equal("vultr/llama-3", message.Model);
        Assert.Equal(["/v1/responses", "/v1/messages"], requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.Equal("Bearer", requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("2023-06-01", requests[1].Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task Messages_streaming_uses_the_native_messages_endpoint()
    {
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("", Encoding.UTF8, "text/event-stream")
            };
        });

        await foreach (var _ in provider.MessagesStreamingAsync(
            new MessagesRequest { Model = "llama-3", MaxTokens = 16, Messages = [], Stream = true },
            []))
        {
        }

        Assert.Equal("/v1/messages", path);
    }

    [Fact]
    public async Task Decisions_embeddings_and_rerank_use_documented_native_routes()
    {
        var paths = new List<string>();
        var provider = CreateProvider(request =>
        {
            paths.Add(request.RequestUri?.AbsolutePath ?? "");
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/systemone" => JsonResponse("""
                    {"model":"clef","answers":[{"type":"predicate","name":"q1","probability":0.9}],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}
                    """),
                "/v1/embeddings" => JsonResponse("""
                    {"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],"model":"embed","usage":{"prompt_tokens":2,"total_tokens":2}}
                    """),
                "/v1/rerank" => JsonResponse("""
                    {"results":[{"index":1,"score":0.95},{"index":0,"score":0.3}]}
                    """),
                _ => JsonResponse("{}", HttpStatusCode.NotFound)
            };
        });

        var decision = await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "clef",
            Input = "Some evidence",
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "q1", Instructions = "Is it true?" }]
        });
        var vercelDecision = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "clef",
            State = "Some evidence",
            Questions = new Dictionary<string, DecisionQuestion>
            {
                ["q1"] = new DecisionBooleanQuestion { Instructions = "Is it true?" }
            }
        });
        var embeddings = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "embed",
            Input = JsonSerializer.SerializeToElement("text")
        });
        var vercelEmbeddings = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "embed",
            Values = ["text"]
        });
        var rerank = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "rerank",
            Query = "question",
            Documents = new RerankingDocument
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[] { "first", "second" })
            }
        });

        Assert.Equal(0.9, Assert.IsType<OpenAIDecisionPredicateAnswer>(Assert.Single(decision.Answers)).Probability);
        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(vercelDecision.Answers["q1"]).Probability);
        Assert.Equal(2, Assert.Single(embeddings.Data).Embedding.GetArrayLength());
        Assert.Equal(2, Assert.Single(vercelEmbeddings.Embeddings).Count());
        Assert.Equal(1, rerank.Ranking.First().Index);
        Assert.Equal(
            ["/v1/systemone", "/v1/systemone", "/v1/embeddings", "/v1/embeddings", "/v1/rerank"],
            paths);
    }

    [Fact]
    public async Task Transcription_uses_native_audio_transcriptions_endpoint()
    {
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return JsonResponse("""{"text":"recognized speech"}""");
        });
        var audio = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "speech.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "asr",
            File = audio,
            ResponseFormat = "json"
        });

        Assert.Equal("/v1/audio/transcriptions", path);
        Assert.Equal("recognized speech", response.Text);
    }

    [Fact]
    public async Task Vercel_transcription_uses_native_audio_transcriptions_endpoint()
    {
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return JsonResponse("""{"text":"transcribed"}""");
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "asr",
            Audio = Convert.ToBase64String([1, 2, 3]),
            MediaType = "audio/wav"
        });

        Assert.Equal("/v1/audio/transcriptions", path);
        Assert.Equal("transcribed", response.Text);
    }

    [Fact]
    public async Task Speech_uses_native_audio_speech_endpoint()
    {
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "speech-model/voice-a",
            Text = "hello"
        });

        Assert.Equal("/v1/audio/speech", path);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
    }

    [Fact]
    public async Task Video_operation_polls_and_downloads_native_video_content()
    {
        var paths = new List<string>();
        string? createBody = null;
        var provider = CreateProvider(request =>
        {
            paths.Add(request.RequestUri?.AbsolutePath ?? "");
            if (request.RequestUri?.AbsolutePath == "/v1/videos")
                createBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/videos" => JsonResponse("""{"id":"video_123","status":"queued"}""", HttpStatusCode.Accepted),
                "/v1/videos/video_123" => JsonResponse("""{"id":"video_123","status":"completed"}"""),
                "/v1/videos/video_123/content" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                },
                _ => JsonResponse("{}", HttpStatusCode.NotFound)
            };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "video-model",
            Prompt = "A short clip",
            Duration = 5,
            Resolution = "1280x720"
        });
        var result = await provider.GetVideoOperationStatus(started.Operation);

        var completed = Assert.IsType<VideoOperationCompletedResult>(result);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), Assert.IsType<string>(Assert.Single(completed.Videos).Data));
        using var requestBody = JsonDocument.Parse(createBody!);
        Assert.Equal(5, requestBody.RootElement.GetProperty("seconds").GetInt32());
        Assert.Equal("1280x720", requestBody.RootElement.GetProperty("size").GetString());
        Assert.False(requestBody.RootElement.TryGetProperty("duration", out _));
        Assert.Equal(
            ["/v1/videos", "/v1/videos/video_123", "/v1/videos/video_123/content"],
            paths);
    }

    [Fact]
    public async Task Image_edits_use_vultr_native_image_edit_endpoint()
    {
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return JsonResponse("""{"created":1730000000,"data":[{"b64_json":"aW1hZ2U="}]}""");
        });
        var image = new FormFile(new MemoryStream([1, 2]), 0, 2, "image", "input.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = MediaTypeNames.Image.Png
        };

        var response = await provider.OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
        {
            Model = "flux-edit",
            Prompt = "Change the background",
            ImageFiles = [image]
        });

        Assert.Equal("/v1/images/edits", path);
        Assert.Equal("aW1hZ2U=", Assert.Single(response.Data!).B64Json);
    }

    [Fact]
    public async Task Vercel_image_files_use_native_image_edits_instead_of_being_ignored()
    {
        string? path = null;
        var provider = CreateProvider(request =>
        {
            path = request.RequestUri?.AbsolutePath;
            return JsonResponse("""{"created":1730000000,"data":[{"b64_json":"aW1hZ2U="}]}""");
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "flux-edit",
            Prompt = "Change the background",
            Files =
            [
                new ImageFile
                {
                    MediaType = MediaTypeNames.Image.Png,
                    Data = "aW1hZ2U="
                }
            ]
        });

        Assert.Equal("/v1/images/edits", path);
        Assert.Empty(response.Warnings);
        Assert.Single(response.Images!);
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static VultrProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
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
            => Task.FromResult(responder(request));
    }
}
