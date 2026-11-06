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
    [MemberData(nameof(ConversationCases))]
    public async Task CancellationPropagatesThroughEveryConversationalEndpoint(string providerId, string endpoint)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = Create(providerId, NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeEndpoint(provider, endpoint, cancellation.Token));
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
        var final = Assert.Single(events, item => item.Event.Data is AIToolOutputAvailableEventData { Preliminary: false });
        AssertAudio(Assert.IsType<AIToolOutputAvailableEventData>(final.Event.Data).Output, "audio/mpeg");
    }

    private static async Task<string> InvokeEndpoint(IModelProvider provider, string endpoint, CancellationToken token = default)
    {
        var model = Model(provider.GetIdentifier());
        var chat = Read<ChatCompletionOptions>(new
        {
            model,
            messages = new[] { new { role = "user", content = "Hello" } }
        });
        var responses = Read<ResponseRequest>(new { model, input = "Hello" });
        var messages = Read<MessagesRequest>(new
        {
            model,
            max_tokens = 128,
            messages = new[] { new { role = "user", content = "Hello" } }
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
                id = "speech-conversation",
                model,
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
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                providerId == "neuphonic" ? "text/event-stream" : "application/x-ndjson")
            };
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
