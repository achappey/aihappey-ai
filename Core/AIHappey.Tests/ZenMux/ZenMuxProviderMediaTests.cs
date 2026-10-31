using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.ZenMux;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ZenMux;

public sealed class ZenMuxProviderMediaTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1.123456789012345)]
    public async Task VercelSpeechPreservesDoublePrecisionSpeedAndNull(double? speed)
    {
        JsonElement body = default;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return JsonResponse("""{"audio":"AQID","mime_type":"audio/pcm"}""");
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "google/gemini-3.1-flash-tts-preview", Text = "Hello", Speed = speed
        });
        if (speed is null)
            Assert.False(body.TryGetProperty("speed", out _));
        else
            Assert.Equal(speed.Value, body.GetProperty("speed").GetDouble());
    }

    [Fact]
    public async Task Image_generation_and_edit_use_documented_endpoints_and_payloads()
    {
        var requests = new List<(string Path, string Body)>();
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return JsonResponse("""{"created":1,"data":[{"b64_json":"aW1hZ2U="}]}""");
        });

        var generated = await provider.ImageRequest(new ImageRequest
        {
            Model = "gpt-image-2",
            Prompt = "A small orange cat",
            N = 1,
            Size = "1024x1024"
        });
        await provider.OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
        {
            Model = "gpt-image-2",
            Prompt = "Make the cat blue",
            Images = [new OpenAIImageReference { ImageUrl = "https://example.com/cat.png" }]
        });

        Assert.Equal("data:image/png;base64,aW1hZ2U=", Assert.Single(generated.Images!));
        Assert.Equal("/api/v1/images/generations", requests[0].Path);
        Assert.Equal("/api/v1/images/edits", requests[1].Path);
        using var generation = JsonDocument.Parse(requests[0].Body);
        Assert.Equal("gpt-image-2", generation.RootElement.GetProperty("model").GetString());
        Assert.Equal("b64_json", generation.RootElement.GetProperty("response_format").GetString());
        using var edit = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("https://example.com/cat.png",
            edit.RootElement.GetProperty("images")[0].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task Embeddings_use_openai_compatible_endpoint()
    {
        var requestBody = string.Empty;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""
                {"object":"list","data":[{"object":"embedding","embedding":[0.25,0.5],"index":0}],
                 "model":"openai/text-embedding-3-small","usage":{"prompt_tokens":3,"total_tokens":3}}
                """);
        });

        var result = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "openai/text-embedding-3-small",
            Input = JsonSerializer.SerializeToElement("ZenMux embeddings")
        });

        Assert.Equal(2, Assert.Single(result.Data).Embedding.GetArrayLength());
        using var body = JsonDocument.Parse(requestBody);
        Assert.Equal("openai/text-embedding-3-small", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("ZenMux embeddings", body.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task Speech_decodes_json_audio_and_streams_speech_events()
    {
        var streamRequested = false;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.RootElement.GetProperty("stream").GetBoolean())
            {
                streamRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"type\":\"speech.audio.delta\",\"audio\":\"AQID\",\"mime_type\":\"audio/L16;rate=24000\"}\n\n" +
                        "data: {\"type\":\"speech.audio.done\",\"usage\":{\"input_tokens\":2,\"output_tokens\":3,\"total_tokens\":5}}\n\n" +
                        "data: [DONE]\n\n",
                        Encoding.UTF8,
                        "text/event-stream")
                };
            }

            return JsonResponse("""
                {"model":"google/gemini-3.1-flash-tts-preview","audio":"AQID",
                 "mime_type":"audio/L16;rate=24000","usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}
                """);
        });

        var generated = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "google/gemini-3.1-flash-tts-preview",
            Input = "Hello",
            Voice = "Kore"
        });
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "google/gemini-3.1-flash-tts-preview",
            Input = "Hello",
            Voice = "Kore"
        }))
            events.Add(item);

        Assert.Equal(new byte[] { 1, 2, 3 }, generated.Audio);
        Assert.Equal("audio/L16;rate=24000", generated.MimeType);
        Assert.True(streamRequested);
        Assert.Equal(2, events.Count);
        Assert.IsType<AudioSpeechStreamDelta>(events[0]);
        Assert.IsType<AudioSpeechStreamDone>(events[1]);
    }

    [Fact]
    public async Task Transcription_posts_json_audio_and_maps_token_usage()
    {
        var requestBody = string.Empty;
        var requestPath = string.Empty;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            requestPath = request.RequestUri!.AbsolutePath;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""
                {"text":"recognized words","model":"qwen/qwen3-asr-flash",
                 "usage":{"input_tokens":12,"output_tokens":2,"total_tokens":14,"seconds":3}}
                """);
        });

        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "qwen/qwen3-asr-flash",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["zenmux"] = JsonSerializer.SerializeToElement(new { language = "en", enable_itn = true })
            }
        });

        Assert.Equal("recognized words", result.Text);
        Assert.Equal(3f, result.DurationInSeconds);
        Assert.Equal("/api/v1/audio/transcriptions", requestPath);
        using var body = JsonDocument.Parse(requestBody);
        Assert.Equal("AQID", body.RootElement.GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Equal("wav", body.RootElement.GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal("en", body.RootElement.GetProperty("language").GetString());
        Assert.True(body.RootElement.GetProperty("enable_itn").GetBoolean());
    }

    [Fact]
    public async Task Transcription_stream_maps_incremental_text_events()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"type\":\"transcript.text.delta\",\"delta\":\"ZenMux \"}\n\n" +
                "data: {\"type\":\"transcript.text.delta\",\"delta\":\"ASR\"}\n\n" +
                "data: {\"type\":\"transcript.text.done\",\"text\":\"ZenMux ASR\",\"usage\":{\"seconds\":2}}\n\n" +
                "data: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream")
        }));
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var item in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
        {
            Model = "qwen/qwen3-asr-flash",
            File = CreateAudioFile()
        }))
            events.Add(item);

        Assert.Equal(3, events.Count);
        Assert.Equal("ZenMux ", Assert.IsType<OpenAITranscriptionTextDelta>(events[0]).Delta);
        Assert.Equal("ZenMux ASR", Assert.IsType<OpenAITranscriptionTextDone>(events[2]).Text);
    }

    [Fact]
    public async Task Reranking_sends_zenmux_nested_input_and_parameters()
    {
        var requestBody = string.Empty;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""
                {"object":"list","model":"qwen/qwen3-rerank",
                 "results":[{"index":1,"relevance_score":0.93}],
                 "usage":{"prompt_tokens":20,"total_tokens":20}}
                """);
        });

        var result = await provider.RerankingRequest(new RerankingRequest
        {
            Model = "qwen/qwen3-rerank",
            Query = "capital of China",
            Documents = new()
            {
                Type = "text",
                Values = JsonSerializer.SerializeToElement(new[]
                {
                    "Shanghai is a large city.", "Beijing is the capital."
                })
            },
            TopN = 1,
            ProviderOptions = new()
            {
                ["zenmux"] = JsonSerializer.SerializeToElement(new { return_documents = true })
            }
        });

        var ranking = Assert.Single(result.Ranking);
        Assert.Equal(1, ranking.Index);
        Assert.Equal(0.93f, ranking.RelevanceScore);
        using var body = JsonDocument.Parse(requestBody);
        Assert.Equal("capital of China", body.RootElement.GetProperty("input").GetProperty("query").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("input").GetProperty("documents").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("parameters").GetProperty("top_n").GetInt32());
        Assert.True(body.RootElement.GetProperty("parameters").GetProperty("return_documents").GetBoolean());
    }

    [Fact]
    public async Task Decisions_posts_system_one_request_and_maps_answers()
    {
        var requestBody = string.Empty;
        var requestPath = string.Empty;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            requestPath = request.RequestUri!.AbsolutePath;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""
                {"model":"typesafe/jev-latest",
                 "answers":{"flag":{"type":"noul","noul":0.87}},
                 "usage":{"input_tokens":10,"output_tokens":2}}
                """);
        });

        var result = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "typesafe/jev-latest",
            State = "A refund was requested.",
            Questions = new()
            {
                ["flag"] = new DecisionBooleanQuestion { Instructions = "Is this about a refund?" }
            }
        });

        Assert.Equal(0.87, Assert.IsType<DecisionBooleanAnswer>(result.Answers["flag"]).Probability);
        Assert.Equal(10, result.Usage!.InputTokens);
        using var body = JsonDocument.Parse(requestBody);
        Assert.Equal("typesafe/jev-latest", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("A refund was requested.", body.RootElement.GetProperty("state").GetString());
        Assert.Equal("/api/v1/systemone", requestPath);
        Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("flag").GetProperty("type").GetString());
    }

    private static IFormFile CreateAudioFile()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "speech.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };
        return file;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static ZenMuxProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
