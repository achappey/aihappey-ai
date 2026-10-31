using System.Net;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AbacusAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AbacusAI;

public sealed class AbacusAIProviderMediaTests
{
    [Theory]
    [InlineData("gpt-audio-1.5", "alloy", "mp3", "audio/mpeg")]
    [InlineData("gpt-audio-mini", "shimmer", "wav", "audio/wav")]
    [InlineData("gemini-2.5-flash-preview-tts", "nova", "opus", "audio/opus")]
    [InlineData("gemini-2.5-pro-preview-tts", "echo", "flac", "audio/flac")]
    public async Task Speech_maps_models_voices_formats_and_preserves_options_and_usage(
        string model, string voice, string format, string mime)
    {
        var provider = Provider(async (request, ct) =>
        {
            var body = await Body(request, ct);
            Assert.Equal(model, body.GetProperty("model").GetString());
            Assert.Equal(new[] { "text", "audio" }, body.GetProperty("modalities").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(voice, body.GetProperty("audio").GetProperty("voice").GetString());
            Assert.Equal(format, body.GetProperty("audio").GetProperty("format").GetString());
            Assert.True(body.GetProperty("audio").GetProperty("future_option").GetBoolean());
            Assert.Equal(17, body.GetProperty("future_root").GetInt32());
            Assert.Contains("whisper", body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            return Json($$$$"""
                {"id":"speech","model":"{{{{model}}}}","created":1,"choices":[{"message":{"audio":{"data":"AQIDBA==","format":"{{{{format}}}}"}}}],
                "usage":{"prompt_tokens":2,"completion_tokens":4,"total_tokens":6,"compute_points_used":12,"cost":0.01}}
                """);
        });
        var result = await provider.SpeechRequest(new()
        {
            Model = $"abacusai/{model}", Text = "hello", Voice = voice, OutputFormat = mime,
            Instructions = "whisper", ProviderOptions = Options("""{"audio":{"future_option":true},"future_root":17}""")
        });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Convert.FromBase64String(result.Audio.Base64));
        Assert.Equal(mime, result.Audio.MimeType);
        Assert.Equal($"abacusai/{model}", result.Response.ModelId);
        Assert.Equal(12, result.ProviderMetadata!["abacusai"].GetProperty("usage").GetProperty("compute_points_used").GetInt32());
        Assert.Equal(0.01m, result.ProviderMetadata["gateway"].GetProperty("cost").GetDecimal());
    }

    [Theory]
    [InlineData("A", "QIDB", "A==")]
    [InlineData("AQID", "BA", "")]
    [InlineData("AQ==", "Ag==", "AwQ=")]
    public async Task Speech_stream_decodes_arbitrary_base64_boundaries_and_emits_one_finish(string a, string b, string c)
    {
        var provider = Provider(async (request, ct) =>
        {
            var body = await Body(request, ct);
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal("mid-word", body.GetProperty("messages")[1].GetProperty("content").GetString());
            return Sse(string.Join("\n\n", new[]
            {
                ": keepalive",
                Chunk(a), Chunk(b), Chunk(c),
                """data: {"model":"gpt-audio-mini","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
                """data: {"model":"gpt-audio-mini","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":5,"total_tokens":8,"compute_points_used":10}}""",
                "data: [DONE]", ""
            }));
        });
        var parts = await Collect(provider.SpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Text = [new() { Text = "mid-" }, new() { Text = "word" }], IncludeRawChunks = true
        }));
        Assert.IsType<SpeechStreamStartPart>(parts[0]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, parts.OfType<SpeechAudioPart>().SelectMany(p => Convert.FromBase64String(p.Audio)).ToArray());
        Assert.All(parts.OfType<SpeechAudioPart>(), p => Assert.Equal("audio/mpeg", p.AudioFormat.Type));
        Assert.NotEmpty(parts.OfType<SpeechRawPart>());
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Equal(3, finish.Usage!.InputTokens);
        Assert.Equal(5, finish.Usage.OutputTokens);
        Assert.Equal(10, finish.ProviderMetadata!["abacusai"].GetProperty("usage").GetProperty("compute_points_used").GetInt32());
        Assert.Same(finish, parts[^1]);
    }

    [Theory]
    [InlineData("!", true)]
    [InlineData("A", true)]
    [InlineData("", true)]
    [InlineData("AQID", false)]
    public async Task Speech_stream_errors_do_not_emit_finish(string data, bool stopped)
    {
        var events = Chunk(data) + "\n\n"
            + (stopped ? """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n" : "")
            + "data: [DONE]\n\n";
        var provider = Provider((_, _) => Task.FromResult(Sse(events)));
        var parts = new List<StreamingSpeechPart>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var part in provider.SpeechStreamingAsync(new() { Model = "gpt-audio-mini", Text = [new() { Text = "hello" }] }))
                parts.Add(part);
        });
        Assert.Empty(parts.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task Speech_stream_provider_error_and_cancellation_propagate()
    {
        var provider = Provider((_, _) => Task.FromResult(Sse("""data: {"error":{"message":"no audio capability"}}""" + "\n\n")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.SpeechStreamingAsync(new()
        {
            Model = "unsupported", Text = [new() { Text = "hello" }]
        })));
        using var cts = new CancellationTokenSource();
        var cancelled = Provider((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(cancelled.SpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Text = [new() { Text = "hello" }]
        }, cts.Token)));
    }

    [Fact]
    public async Task Speech_audio_is_emitted_before_the_upstream_response_completes()
    {
        var pipe = new Pipe();
        var provider = Provider(async (_, ct) =>
        {
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Chunk("AQIDB") + "\n\n"), ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream())
                {
                    Headers = { ContentType = new("text/event-stream") }
                }
            };
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = provider.SpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Text = [new() { Text = "hello" }]
        }, timeout.Token).GetAsyncEnumerator();
        while (await stream.MoveNextAsync() && stream.Current is not SpeechAudioPart)
        {
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(Assert.IsType<SpeechAudioPart>(stream.Current).Audio));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Chunk("A==") + "\n\n"
            + """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n"
            + "data: [DONE]\n\n"), timeout.Token);
        await pipe.Writer.CompleteAsync();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(new byte[] { 4 }, Convert.FromBase64String(Assert.IsType<SpeechAudioPart>(stream.Current).Audio));
        Assert.True(await stream.MoveNextAsync());
        Assert.IsType<SpeechFinishPart>(stream.Current);
        Assert.False(await stream.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_after_first_audio_does_not_emit_a_finish()
    {
        var provider = Provider((_, _) => Task.FromResult(Sse(Chunk("AQID") + "\n\n"
            + """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""" + "\n\n")));
        using var cancellation = new CancellationTokenSource();
        await using var stream = provider.SpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Text = [new() { Text = "hello" }]
        }, cancellation.Token).GetAsyncEnumerator();
        while (await stream.MoveNextAsync() && stream.Current is not SpeechAudioPart)
        {
        }
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    [Theory]
    [InlineData("audio/mpeg", "mp3")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/pcm", "pcm")]
    [InlineData("audio/webm", "webm")]
    public async Task Transcription_uses_input_audio_and_verbatim_instructions(string mediaType, string format)
    {
        var provider = Provider(async (request, ct) =>
        {
            var body = await Body(request, ct);
            var content = body.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("input_audio", content[0].GetProperty("type").GetString());
            Assert.Equal("AQID", content[0].GetProperty("input_audio").GetProperty("data").GetString());
            Assert.Equal(format, content[0].GetProperty("input_audio").GetProperty("format").GetString());
            var instructions = content[1].GetProperty("text").GetString();
            Assert.Contains("verbatim", instructions);
            Assert.Contains("Dutch", instructions);
            Assert.Contains("meeting", instructions);
            Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
            Assert.True(body.GetProperty("future_transcription").GetBoolean());
            return Json("""{"model":"gemini-2.5-flash","choices":[{"message":{"content":"Hallo."}}],"usage":{"compute_points_used":7}}""");
        });
        var response = await provider.TranscriptionRequest(new()
        {
            Model = "abacusai/gemini-2.5-flash", Audio = $"data:{mediaType};base64,AQID", MediaType = mediaType,
            ProviderOptions = Options("""{"language":"Dutch","prompt":"meeting","temperature":0.2,"future_transcription":true}""")
        });
        Assert.Equal("Hallo.", response.Text);
        Assert.Empty(response.Segments);
        Assert.Null(response.DurationInSeconds);
        Assert.Equal(7, response.ProviderMetadata!["abacusai"].GetProperty("usage").GetProperty("compute_points_used").GetInt32());
    }

    [Fact]
    public async Task Transcription_stream_is_explicitly_emulated_with_only_final_text()
    {
        var provider = Provider(async (request, ct) =>
        {
            Assert.False((await Body(request, ct)).GetProperty("stream").GetBoolean());
            return Json("""{"choices":[{"message":{"content":"Complete transcript"}}],"usage":{"prompt_tokens":10}}""");
        });
        var parts = await Collect(provider.TranscriptionStreamingAsync(new()
        {
            Model = "gpt-audio-1.5", Audio = "AQID", InputAudioFormat = new() { Type = "wav" }, IncludeRawChunks = true
        }));
        Assert.NotEmpty(Assert.IsType<TranscriptionStreamStartPart>(parts[0]).Warnings);
        Assert.Empty(parts.OfType<TranscriptionPartialPart>());
        Assert.Empty(parts.OfType<TranscriptionDeltaPart>());
        Assert.Equal("Complete transcript", Assert.Single(parts.OfType<TranscriptionFinalPart>()).Text);
        Assert.True(Assert.Single(parts.OfType<TranscriptionFinishPart>()).ProviderMetadata!["abacusai"].GetProperty("emulated").GetBoolean());
        Assert.Single(parts.OfType<TranscriptionRawPart>());
    }

    [Theory]
    [InlineData("flux2_pro", true)]
    [InlineData("nano_banana2", true)]
    [InlineData("gpt_image15_edit", false)]
    [InlineData("flux_kontext_edit", false)]
    public async Task Images_map_generation_and_edit_inputs_and_model_config(string model, bool countSupported)
    {
        var provider = Provider(async (request, ct) =>
        {
            var body = await Body(request, ct);
            Assert.Equal("image", body.GetProperty("modalities")[0].GetString());
            Assert.Equal("data:image/png;base64,AQID", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("https://example.com/reference.png", body.GetProperty("messages")[0].GetProperty("content")[2].GetProperty("image_url").GetProperty("url").GetString());
            var config = body.GetProperty("image_config");
            Assert.Equal(countSupported, config.TryGetProperty("num_images", out _));
            Assert.Equal("edit me", config.GetProperty("prompt").GetString());
            Assert.Equal("2:3", config.GetProperty("aspect_ratio").GetString());
            Assert.Equal("2K", config.GetProperty("resolution").GetString());
            Assert.Equal(42, config.GetProperty("seed").GetInt32());
            Assert.True(config.GetProperty("future_edit").GetBoolean());
            return Json("""{"model":"returned-model","created":1,"choices":[{"message":{"images":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}}],"usage":{"compute_points_used":150}}""");
        });
        var response = await provider.ImageRequest(new()
        {
            Model = $"abacusai/{model}", Prompt = "edit me", N = 1, AspectRatio = "2:3", Seed = 42,
            Files = [new() { Data = "AQID", MediaType = "image/png" }, new ImageFileUrl { Url = "https://example.com/reference.png" }],
            ProviderOptions = Options("""{"image_config":{"resolution":"2K","future_edit":true}}""")
        });
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
        Assert.Equal("abacusai/returned-model", response.Response.ModelId);
        Assert.Equal(150, response.ProviderMetadata!["abacusai"].GetProperty("usage").GetProperty("compute_points_used").GetInt32());
    }

    [Fact]
    public async Task Image_urls_are_downloaded_without_provider_authorization()
    {
        var provider = Provider((_, _) => Task.FromResult(Json("""{"choices":[{"message":{"images":[{"type":"image_url","image_url":{"url":"https://8.8.8.8/output.png"}}]}}]}""")),
            (request, _) =>
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("https://8.8.8.8/output.png", request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("image/png") } }
                });
            });
        Assert.Equal("data:image/png;base64,AQID", Assert.Single((await provider.ImageRequest(new() { Model = "flux_pro", Prompt = "draw" })).Images!));
    }

    [Fact]
    public async Task Private_image_urls_are_rejected_before_download()
    {
        var provider = Provider((_, _) => Task.FromResult(Json("""{"choices":[{"message":{"images":[{"type":"image_url","image_url":{"url":"http://127.0.0.1/private"}}]}}]}""")),
            (_, _) => throw new InvalidOperationException("Must not download"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new() { Model = "flux_pro", Prompt = "draw" }));
    }

    [Fact]
    public async Task Image_download_errors_are_not_hidden()
    {
        var provider = Provider((_, _) => Task.FromResult(Json("""{"choices":[{"message":{"images":[{"type":"image_url","image_url":{"url":"https://8.8.8.8/output.png"}}]}}]}""")),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ImageRequest(new() { Model = "flux_pro", Prompt = "draw" }));
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"error":{"message":"invalid model"}}""")]
    [InlineData("""{"choices":[{"message":{"refusal":"denied"}}]}""")]
    public async Task Malformed_or_refused_responses_are_not_success(string json)
    {
        var provider = Provider((_, _) => Task.FromResult(Json(json)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new() { Model = "gpt-audio-mini", Text = "hello" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscriptionRequest(new() { Model = "gpt-audio-mini", Audio = "AQID", MediaType = "wav" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new() { Model = "flux_pro", Prompt = "draw" }));
    }

    [Fact]
    public async Task Http_errors_preserve_status_and_provider_message()
    {
        var provider = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("quota exceeded") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(new() { Model = "gpt-audio-mini", Text = "hello" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("quota exceeded", error.Message);
    }

    [Fact]
    public async Task Invalid_inputs_and_conflicting_speech_parts_fail_before_http()
    {
        var provider = Provider((_, _) => throw new InvalidOperationException("Must not send"));
        await Assert.ThrowsAsync<FormatException>(() => provider.TranscriptionRequest(new() { Model = "gpt-audio-mini", Audio = "!", MediaType = "wav" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new() { Model = "gpt-audio-mini", Text = "hello", OutputFormat = "webm" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider.SpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Text = [new() { Text = "a", Voice = "alloy" }, new() { Text = "b", Voice = "nova" }]
        })));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider.SpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Text = [new() { Text = "a", ProviderOptions = Options("""{"temperature":0}""") }, new() { Text = "b" }]
        })));
    }

    [Fact]
    public async Task OpenAI_contracts_use_the_same_media_paths()
    {
        var provider = Provider(async (request, ct) =>
        {
            var body = await Body(request, ct);
            if (body.TryGetProperty("audio", out _))
                return Json("""{"choices":[{"message":{"audio":{"data":"AQID","format":"mp3"}}}]}""");
            if (body.TryGetProperty("image_config", out _))
                return Json("""{"choices":[{"message":{"images":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}}]}""");
            return Json("""{"choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""");
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, (await provider.OpenAISpeechRequestAsync(new() { Model = "gpt-audio-mini", Input = "hello" })).Audio);
        Assert.Equal("AQID", Assert.Single((await provider.OpenAIImageGenerationRequestAsync(new() { Model = "flux_pro", Prompt = "hello" })).Data!).B64Json);
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "audio.wav") { Headers = new HeaderDictionary(), ContentType = "audio/wav" };
        var transcription = await provider.OpenAITranscriptionRequestAsync(new() { Model = "gpt-audio-mini", File = file });
        Assert.Equal("hello", transcription.Text);
        Assert.Equal(3, Assert.IsType<OpenAITranscriptionTokenUsage>(Assert.IsType<OpenAITranscriptionResponse>(transcription).Usage).TotalTokens);
        var stream = await Collect(provider.OpenAITranscriptionStreamingAsync(new() { Model = "gpt-audio-mini", File = file }));
        Assert.IsType<OpenAITranscriptionTextDone>(Assert.Single(stream));
    }

    [Fact]
    public async Task OpenAI_edit_accepts_multipart_images_and_raw_model_configuration()
    {
        var provider = Provider(async (request, ct) =>
        {
            var body = await Body(request, ct);
            Assert.Equal("data:image/png;base64,AQID", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("high", body.GetProperty("image_config").GetProperty("quality").GetString());
            Assert.True(body.GetProperty("image_config").GetProperty("future_edit").GetBoolean());
            return Json("""{"choices":[{"message":{"images":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}}]}""");
        });
        var image = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "image", "image.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png"
        };
        var response = await provider.OpenAIImageEditRequestAsync(new()
        {
            Model = "flux_kontext_edit", Prompt = "edit", ImageFiles = [image], Quality = "high",
            AdditionalProperties = new() { ["image_config"] = JsonSerializer.Deserialize<JsonElement>("""{"future_edit":true}""") }
        });
        Assert.Equal("AQID", Assert.Single(response.Data!).B64Json);
    }

    [Fact]
    public async Task OpenAI_streaming_speech_maps_audio_and_token_usage()
    {
        var provider = Provider((_, _) => Task.FromResult(Sse(Chunk("AQID") + "\n\n"
            + """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}"""
            + "\n\ndata: [DONE]\n\n")));
        var parts = await Collect(provider.OpenAISpeechStreamingAsync(new()
        {
            Model = "gpt-audio-mini", Input = "hello", StreamFormat = "sse"
        }));
        Assert.Equal("AQID", Assert.Single(parts.OfType<AudioSpeechStreamDelta>()).Audio);
        Assert.Equal(5, Assert.Single(parts.OfType<AudioSpeechStreamDone>()).Usage!.TotalTokens);
    }

    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
        Assert.Equal("secret-key", request.Headers.Authorization!.Parameter);
        return JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Options(string json)
        => new() { ["abacusai"] = JsonSerializer.Deserialize<JsonElement>(json) };

    private static string Chunk(string data)
        => "data: " + JsonSerializer.Serialize(new { model = "gpt-audio-mini", choices = new[] { new { delta = new { audio = new { data, format = "mp3" } } } } });

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string events) => new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var parts = new List<T>();
        await foreach (var part in stream)
            parts.Add(part);
        return parts;
    }

    private static AbacusAIProvider Provider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? download = null)
        => new(new Keys(), new Clients(responder, download ?? responder), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "secret-key";
    }

    private sealed class Clients(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> api,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> download) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(name == "mcp-provider-image-output" ? download : api));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }
}
