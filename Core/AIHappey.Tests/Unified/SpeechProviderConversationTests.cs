using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Narakeet;
using AIHappey.Core.Providers.Neuphonic;
using AIHappey.Core.Providers.Respeecher;
using AIHappey.Core.Providers.WellSaid;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Unified;

public sealed class SpeechProviderConversationTests
{
    private static readonly string[] Providers = ["wellsaid", "respeecher", "narakeet", "neuphonic"];
    private static readonly string[] Endpoints =
        ["chat", "chat-stream", "responses", "responses-stream", "messages", "messages-stream", "api-chat"];
    private static readonly byte[] Audio = Enumerable.Range(0, 9000).Select(value => (byte)value).ToArray();

    public static IEnumerable<object[]> ConversationCases()
        => Providers.SelectMany(provider => Endpoints.Select(endpoint => new object[] { provider, endpoint }));

    public static IEnumerable<object[]> UnifiedCases()
        => Providers.SelectMany(provider => new[] { false, true }.Select(streaming => new object[] { provider, streaming }));

    [Theory]
    [MemberData(nameof(ConversationCases))]
    public async Task EveryConversationalEndpointReturnsGeneratedSpeech(string providerId, string endpoint)
    {
        var calls = 0;
        var streaming = endpoint.EndsWith("stream", StringComparison.Ordinal) || endpoint == "api-chat";
        var provider = Create(providerId, async (message, token) =>
        {
            calls++;
            await AssertNativeRequest(providerId, message, token, streaming);
            return NativeAudio(providerId, streaming);
        });

        var output = await InvokeEndpoint(provider, endpoint);

        Assert.Equal(1, calls);
        Assert.Contains("generate_speech", output);
        Assert.Contains("byteLength", output);
        Assert.Contains(MimeType(providerId, streaming), output);
        Assert.True(ContainsAudio(JsonSerializer.Deserialize<JsonElement>(output)), "The endpoint omitted the complete generated audio payload.");
        Assert.DoesNotContain("tool-output-error", output);
    }

    [Theory]
    [MemberData(nameof(UnifiedCases))]
    public async Task UnifiedSpeechPreservesAudioOptionsAndRequestWithoutDiscovery(string providerId, bool streaming)
    {
        var calls = 0;
        var provider = Assert.IsAssignableFrom<IUnifiedModelProvider>(Create(providerId, async (message, token) =>
        {
            calls++;
            await AssertNativeRequest(providerId, message, token, streaming, expectNativeOption: true);
            return NativeAudio(providerId, streaming);
        }));
        var request = UnifiedRequest(providerId, new()
        {
            [providerId] = JsonSerializer.SerializeToElement(new { future_option = "kept" }),
            ["other"] = JsonSerializer.SerializeToElement(new { unrelated = true })
        });
        var original = JsonSerializer.Serialize(request);

        if (streaming)
        {
            var events = await Drain(provider.StreamUnifiedAsync(request));
            Assert.All(events, item => Assert.Equal(providerId, item.ProviderId));
            Assert.Equal("tool-input-available", events[0].Event.Type);
            var input = Assert.IsType<AIToolInputAvailableEventData>(events[0].Event.Data);
            Assert.Equal("generate_speech", input.ToolName);
            Assert.True(input.ProviderExecuted);
            var outputs = events.Where(item => item.Event.Type == "tool-output-available")
                .Select(item => Assert.IsType<AIToolOutputAvailableEventData>(item.Event.Data)).ToArray();
            Assert.Contains(outputs, item => item.Preliminary == true);
            var final = Assert.Single(outputs, item => item.Preliminary == false);
            Assert.True(final.ProviderExecuted);
            AssertAudio(final.Output, MimeType(providerId, streaming));
            Assert.Equal("finish", events[^1].Event.Type);
            Assert.Equal("tool-calls", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);
            Assert.All(events, item => Assert.Equal(events[0].Event.Id, item.Event.Id));
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal(providerId, response.ProviderId);
            Assert.Equal(request.Model, response.Model);
            Assert.Equal("completed", response.Status);
            var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(response.Output!.Items!).Content!));
            Assert.Equal("generate_speech", tool.ToolName);
            Assert.True(tool.ProviderExecuted);
            Assert.Equal("output-available", tool.State);
            AssertAudio(tool.Output, MimeType(providerId, streaming));

            var replay = new AIRequest
            {
                Model = request.Model,
                Input = new AIInput
                {
                    Items = [new AIInputItem { Role = "assistant", Content = [tool] }]
                }
            };
            // Returning a generated result must not turn it into a client tool call
            // or replay synthetic server-tool blocks to another model on the next turn.
            Assert.DoesNotContain("generate_speech", JsonSerializer.Serialize(replay.ToChatCompletionOptions(providerId)));
            Assert.DoesNotContain("generate_speech", JsonSerializer.Serialize(replay.ToMessagesRequest(providerId)));
            Assert.DoesNotContain("generate_speech", JsonSerializer.Serialize(replay.ToResponseRequest(providerId)));
        }

        Assert.Equal(1, calls);
        Assert.Equal(original, JsonSerializer.Serialize(request));
    }

    [Theory]
    [MemberData(nameof(UnifiedCases))]
    public async Task ExplicitUnsupportedFormatsAreNotReplacedByNativeDefaults(string providerId, bool streaming)
    {
        var provider = Assert.IsAssignableFrom<IUnifiedModelProvider>(Create(providerId, NoNetwork));
        var request = UnifiedRequest(providerId, new() { ["response_format"] = "flac" });

        if (streaming)
        {
            var events = await Drain(provider.StreamUnifiedAsync(request));
            Assert.Contains(events, item => item.Event.Type == "tool-output-error");
            Assert.DoesNotContain(events, item => item.Event.Data is AIToolOutputAvailableEventData { Preliminary: false });
            Assert.Equal("error", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);
        }
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(request));
    }

    [Theory]
    [InlineData("response_format", false)]
    [InlineData("outputFormat", false)]
    [InlineData("response_format", true)]
    [InlineData("outputFormat", true)]
    public async Task ExplicitSupportedFormatsOverrideConversationDefaults(string metadataKey, bool streaming)
    {
        var provider = Assert.IsAssignableFrom<IUnifiedModelProvider>(Create("narakeet", (message, _) =>
        {
            Assert.Equal("/text-to-speech/m4a", message.RequestUri!.AbsolutePath);
            return Task.FromResult(NativeAudio("narakeet", streaming: false));
        }));
        var request = UnifiedRequest("narakeet", new() { [metadataKey] = "m4a" });
        if (streaming)
        {
            var events = await Drain(provider.StreamUnifiedAsync(request));
            var final = Assert.Single(events.Where(item => item.Event.Data is AIToolOutputAvailableEventData { Preliminary: false }));
            AssertAudio(Assert.IsType<AIToolOutputAvailableEventData>(final.Event.Data).Output, "audio/mp4");
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(response.Output!.Items!).Content!));
            AssertAudio(tool.Output, "audio/mp4");
        }
    }

    [Theory]
    [MemberData(nameof(ConversationCases))]
    public async Task CancellationPropagatesThroughEveryConversationalEndpoint(string providerId, string endpoint)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(providerId, NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeEndpoint(provider, endpoint, cancellation.Token));
    }

    [Theory]
    [MemberData(nameof(UnifiedCases))]
    public async Task NativeFailuresDoNotProduceSuccessfulSpeech(string providerId, bool streaming)
    {
        var provider = Assert.IsAssignableFrom<IUnifiedModelProvider>(Create(providerId, (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("native denied") })));
        var request = UnifiedRequest(providerId);
        if (streaming)
        {
            var events = await Drain(provider.StreamUnifiedAsync(request));
            var error = Assert.Single(events, item => item.Event.Type == "tool-output-error");
            Assert.Contains("native denied", Assert.IsType<AIToolOutputErrorEventData>(error.Event.Data).ErrorText);
            Assert.DoesNotContain(events, item => item.Event.Data is AIToolOutputAvailableEventData { Preliminary: false });
            Assert.Equal("error", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(request));
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.Contains("native denied", error.Message);
        }
    }

    [Theory]
    [InlineData("wellsaid")]
    [InlineData("respeecher")]
    [InlineData("narakeet")]
    [InlineData("neuphonic")]
    public async Task CancellationAfterStreamingAudioDoesNotEmitCompletion(string providerId)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Assert.IsAssignableFrom<IUnifiedModelProvider>(Create(providerId,
            (_, _) => Task.FromResult(NativeAudio(providerId, streaming: true))));
        await using var enumerator = provider.StreamUnifiedAsync(UnifiedRequest(providerId), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AIToolInputAvailableEventData>(enumerator.Current.Event.Data);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(Assert.IsType<AIToolOutputAvailableEventData>(enumerator.Current.Event.Data).Preliminary);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
                Assert.IsNotType<AIFinishEventData>(enumerator.Current.Event.Data);
        });
    }

    [Fact]
    public async Task SharedAdapterRetainsLegacyMp3DefaultAndMetadataMapping()
    {
        var provider = Create("wellsaid", async (message, token) =>
        {
            await AssertNativeRequest("wellsaid", message, token, streaming: true);
            var body = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("kept", body.GetProperty("legacy_option").GetString());
            return NativeAudio("wellsaid", streaming: true);
        });
        var request = UnifiedRequest("wellsaid", new() { ["legacy_option"] = "kept" });
        var events = await Drain(provider.StreamUnifiedSpeechAsync(request));
        var final = Assert.Single(events.Where(item => item.Event.Data is AIToolOutputAvailableEventData { Preliminary: false }));
        AssertAudio(Assert.IsType<AIToolOutputAvailableEventData>(final.Event.Data).Output, "audio/mpeg");
    }

    private static async Task<string> InvokeEndpoint(IModelProvider provider, string endpoint, CancellationToken token = default)
    {
        var model = Model(provider.GetIdentifier());
        var chat = Read<ChatCompletionOptions>(new
        {
            model, messages = new[] { new { role = "user", content = "Hello" } }
        });
        var responses = Read<ResponseRequest>(new { model, input = "Hello" });
        var messages = Read<MessagesRequest>(new
        {
            model, max_tokens = 128, messages = new[] { new { role = "user", content = "Hello" } }
        });
        object result = endpoint switch
        {
            "chat" => await provider.CompleteChatAsync(chat, token),
            "chat-stream" => await Drain(provider.CompleteChatStreamingAsync(chat, token)),
            "responses" => await provider.ResponsesAsync(responses, token),
            "responses-stream" => await Drain(provider.ResponsesStreamingAsync(responses, token)),
            "messages" => await provider.MessagesAsync(messages, [], token),
            "messages-stream" => await Drain(provider.MessagesStreamingAsync(messages, [], token)),
            "api-chat" => await Drain(provider.StreamAsync(Read<ChatRequest>(new
            {
                id = "speech-conversation", model,
                messages = new[] { new { id = "user-1", role = "user", parts = new[] { new { type = "text", text = "Hello" } } } }
            }), token)),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
        };
        return JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
    }

    private static AIRequest UnifiedRequest(string providerId, Dictionary<string, object?>? metadata = null) => new()
    {
        Id = "speech-conversation",
        ProviderId = providerId,
        Model = Model(providerId),
        Metadata = metadata,
        Input = new AIInput
        {
            Items =
            [
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Do not synthesize the history" }] },
                new AIInputItem { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "Previous reply" }] },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }
            ]
        }
    };

    private static void AssertAudio(object? output, string mimeType)
    {
        var result = Assert.IsType<CallToolResult>(output);
        Assert.False(result.IsError);
        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(Assert.Single(result.Content)).Resource);
        Assert.Equal(mimeType, resource.MimeType);
        Assert.Equal(Audio, Convert.FromBase64String(Encoding.UTF8.GetString(resource.Blob.Span)));
        Assert.Equal("completed", result.StructuredContent!.Value.GetProperty("status").GetString());
        Assert.Equal(Audio.Length, result.StructuredContent.Value.GetProperty("byteLength").GetInt32());
        Assert.Equal(mimeType, result.StructuredContent.Value.GetProperty("mediaType").GetString());
    }

    private static bool ContainsAudio(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Any(property => ContainsAudio(property.Value));
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(ContainsAudio);
        if (value.ValueKind != JsonValueKind.String)
            return false;

        var text = value.GetString();
        if (text == Convert.ToBase64String(Audio))
            return true;
        // Messages tool results and Responses function outputs carry JSON strings.
        if (text?.StartsWith('{') == true || text?.StartsWith('[') == true)
        {
            using var document = JsonDocument.Parse(text);
            return ContainsAudio(document.RootElement);
        }
        return false;
    }

    private static async Task AssertNativeRequest(string providerId, HttpRequestMessage message, CancellationToken token,
        bool streaming, bool expectNativeOption = false)
    {
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("x-api-key")));
        var text = await message.Content!.ReadAsStringAsync(token);
        if (providerId == "narakeet")
        {
            Assert.Equal("/text-to-speech/mp3", message.RequestUri!.AbsolutePath);
            Assert.Equal("Hello", text);
            Assert.Contains("voice=mickey", message.RequestUri.Query);
            if (expectNativeOption)
                Assert.Contains("future_option=kept", message.RequestUri.Query);
            Assert.DoesNotContain("other", message.RequestUri.Query);
            return;
        }

        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal("Hello", body.GetProperty(providerId == "respeecher" ? "transcript" : "text").GetString());
        Assert.False(body.TryGetProperty("other", out _));
        Assert.False(body.TryGetProperty(providerId, out _));
        if (expectNativeOption)
            Assert.Equal("kept", body.GetProperty("future_option").GetString());
        switch (providerId)
        {
            case "wellsaid":
                Assert.Equal("/v1/tts/stream", message.RequestUri!.AbsolutePath);
                Assert.Equal(3, body.GetProperty("speaker_id").GetInt32());
                break;
            case "respeecher":
                Assert.Equal($"/v1/public/tts/en-rt/tts/{(streaming ? "sse" : "bytes")}", message.RequestUri!.AbsolutePath);
                Assert.Equal("voice-one", body.GetProperty("voice").GetProperty("id").GetString());
                if (streaming)
                {
                    Assert.Equal("pcm_s16le", body.GetProperty("output_format").GetProperty("encoding").GetString());
                    Assert.Equal(24000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
                }
                break;
            case "neuphonic":
                Assert.Equal("/sse/speak/en", message.RequestUri!.AbsolutePath);
                Assert.Equal("voice-one", body.GetProperty("voice_id").GetString());
                Assert.Equal("pcm_linear", body.GetProperty("encoding").GetString());
                break;
        }
    }

    private static HttpResponseMessage NativeAudio(string providerId, bool streaming)
    {
        if (providerId == "neuphonic" || providerId == "respeecher" && streaming)
        {
            var chunks = new[] { Audio[..4500], Audio[4500..] };
            var body = string.Concat(chunks.Select(chunk => providerId == "neuphonic"
                ? $"data: {JsonSerializer.Serialize(new { status_code = 200, data = new { audio = Convert.ToBase64String(chunk) } })}\n\n"
                : $"{JsonSerializer.Serialize(new { type = "chunk", data = Convert.ToBase64String(chunk) })}\n"));
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8,
                providerId == "neuphonic" ? "text/event-stream" : "application/x-ndjson") };
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) };
        response.Content.Headers.ContentType = new(MimeType(providerId, streaming));
        return response;
    }

    private static string MimeType(string providerId, bool streaming) => providerId switch
    {
        "respeecher" => streaming ? "audio/pcm" : "audio/wav",
        "neuphonic" => "audio/pcm",
        _ => "audio/mpeg"
    };

    private static string Model(string providerId) => providerId switch
    {
        "wellsaid" => "wellsaid/3",
        "narakeet" => "narakeet/mickey",
        _ => $"{providerId}/{(providerId == "respeecher" ? "en-rt" : "en")}/voice-one"
    };

    private static T Read<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), JsonSerializerOptions.Web)!;

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
            items.Add(item);
        return items;
    }

    private static IModelProvider Create(string providerId, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var resolver = new Resolver();
        var factory = new Factory(new Handler(responder));
        return providerId switch
        {
            "wellsaid" => new WellSaidProvider(resolver, factory),
            "respeecher" => new RespeecherProvider(resolver, factory),
            "narakeet" => new NarakeetProvider(resolver, factory),
            "neuphonic" => new NeuphonicProvider(resolver, factory),
            _ => throw new ArgumentOutOfRangeException(nameof(providerId))
        };
    }

    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");

    private sealed class Resolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
