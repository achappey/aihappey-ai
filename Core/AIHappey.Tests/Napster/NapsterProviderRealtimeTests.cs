using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Napster;

namespace AIHappey.Tests.Napster;

public sealed class NapsterProviderRealtimeTests
{
    private const string ServerKey = "fixture-server-credential";
    private const string RtcDetails = """{"connection":{"id":"connection-1","signalingEndpoint":"wss://signal.example.test/session?token=short-lived","iceServers":[{"urls":"turn:relay.example.test","username":"ephemeral","credential":"temporary"}]},"expiresAt":"2099-01-01T00:00:00Z","future":{"enabled":false}}""";
    private const string SocketDetails = """{"url":"https://socket.example.test/session?token=short-lived&extra=1","token":"short-lived","connection":{"id":"connection-1"},"expiresAt":"2099-01-01T00:00:00Z","future":[1,2]}""";
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Native(string details = RtcDetails) => JsonSerializer.Serialize(new
    {
        token = Encode(details),
        connection = new { id = "connection-1", status = "preparing", future = new { supported = true } },
        future = new object[] { "native", false, 42 }
    });
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
    private static RealtimeRequest Request(string options = """{"agentId":"agent_1","channelType":"webrtc"}""", string model = "napster/agent")
        => new() { Model = model, ProviderOptions = new() { ["napster"] = Element(options) } };

    [Theory]
    [InlineData("napster/agent")]
    [InlineData("agent")]
    public async Task AgentCreationUsesServerKeyAndPreservesNativeMetadata(string model)
    {
        var calls = 0;
        var provider = Provider(async (message, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://companion-api.napster.com/public/agents/agent_1/connections", message.RequestUri!.AbsoluteUri);
            Assert.Equal(ServerKey, Assert.Single(message.Headers.GetValues("X-Api-Key")));
            Assert.Null(message.Headers.Authorization);
            Assert.Equal("2026-01-01", Assert.Single(message.Headers.GetValues("X-API-Version")));
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            var payload = Element(await message.Content.ReadAsStringAsync(ct));
            Assert.False(payload.TryGetProperty("agentId", out _));
            Assert.False(payload.TryGetProperty("apiVersion", out _));
            Assert.Equal("webrtc", payload.GetProperty("channelType").GetString());
            Assert.Equal("user_1", payload.GetProperty("externalClientId").GetString());
            Assert.Equal("hello", payload.GetProperty("initialSpeech").GetString());
            Assert.Equal("premium", payload.GetProperty("externalClientProfile").GetProperty("plan").GetString());
            Assert.Equal("en", payload.GetProperty("mcp").GetProperty("future").GetString());
            Assert.False(payload.GetProperty("tags").GetProperty("demo").GetBoolean());
            Assert.Equal(7, payload.GetProperty("futureOption").GetInt32());
            Assert.DoesNotContain(ServerKey, payload.GetRawText());
            return Json(Native());
        });
        var request = Request("""{"agentId":"agent_1","channelType":"webrtc","apiVersion":"2026-01-01","externalClientId":"user_1","initialSpeech":"hello","externalClientProfile":{"plan":"premium"},"mcp":{"future":"en"},"tags":{"demo":false},"futureOption":7}""", model);
        var originalOptions = request.ProviderOptions!["napster"].GetRawText();
        var result = await provider.GetRealtimeToken(request);
        Assert.Equal(1, calls);
        Assert.Equal(Encode(RtcDetails), result.Value);
        Assert.Equal(4070908800, result.ExpiresAt);
        var roundtrip = JsonSerializer.Deserialize<RealtimeResponse>(JsonSerializer.Serialize(result))!;
        var metadata = roundtrip.ProviderMetadata!["napster"];
        Assert.True(JsonElement.DeepEquals(Element(Native()), metadata.GetProperty("response")));
        Assert.True(JsonElement.DeepEquals(Element(RtcDetails), metadata.GetProperty("connectionDetails")));
        Assert.Equal(originalOptions, request.ProviderOptions["napster"].GetRawText());
        Assert.DoesNotContain(ServerKey, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("text")]
    public async Task WebsocketPreservesNativeUrlWithoutAppendingOrRewriting(string modality)
    {
        var provider = Provider(async (message, ct) =>
        {
            var payload = Element(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal(modality, payload.GetProperty("modality").GetString());
            Assert.False(message.Headers.Contains("X-API-Version"));
            return Json(Native(SocketDetails));
        });
        var result = await provider.GetRealtimeToken(Request($$"""{"agentId":"agent_1","channelType":"websocket","modality":"{{modality}}"}"""));
        Assert.Equal("https://socket.example.test/session?token=short-lived&extra=1",
            result.ProviderMetadata!["napster"].GetProperty("connectionDetails").GetProperty("url").GetString());
        Assert.Equal(Encode(SocketDetails), result.Value);
    }

    [Fact]
    public async Task CompanionCreationPassesNativeOptionsWithoutAgentMappings()
    {
        var options = """{"companionId":"persona_1","providerConfig":{"voiceId":"alloy","settings":{"temperature":0.7,"turnDetection":{"threshold":0.9}}},"mode":"puppeteer","videoPolicy":"deferred","disableIdleTimeout":false,"useWebSearch":true,"functions":["fn_1"],"faqCollections":["faq_1"],"knowledgeBaseId":"kb_1","language":"nl","tags":{"env":"test"}}""";
        var provider = Provider(async (message, ct) =>
        {
            Assert.Equal("/public/connections", message.RequestUri!.AbsolutePath);
            Assert.True(JsonElement.DeepEquals(Element(options), Element(await message.Content!.ReadAsStringAsync(ct))));
            return Json(Native());
        });
        _ = await provider.GetRealtimeToken(Request(options, "companion"));
    }

    [Fact]
    public async Task LiveSessionUsesExistingAdditionalTransportAndRoundtripsEveryNativeField()
    {
        var provider = Provider((_, _) => Task.FromResult(Json(Native())));
        var request = JsonSerializer.Deserialize<LiveSessionRequest>("""{"session":{"model":"napster/agent","agentId":"agent_1","channelType":"webrtc","tags":{"env":"test"}},"transport":{"type":"napster"}}""")!;
        var result = await provider.CreateLiveSessionAsync(request);
        Assert.Equal("connection-1", result.Session.Id);
        var roundtrip = JsonSerializer.Deserialize<LiveSessionResponse>(JsonSerializer.Serialize(result))!;
        var transport = Assert.IsType<LiveAdditionalResponseTransport>(roundtrip.Transport);
        Assert.Equal("napster", transport.Type);
        Assert.True(JsonElement.DeepEquals(Element(Native()), transport.AdditionalProperties!["response"]));
        Assert.True(JsonElement.DeepEquals(Element(RtcDetails), transport.AdditionalProperties["connectionDetails"]));
        Assert.DoesNotContain(ServerKey, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"agentId":12,"channelType":"webrtc"}""")]
    [InlineData("""{"agentId":"..","channelType":"webrtc"}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"sip"}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"webrtc","companionId":"other"}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"webrtc","providerConfig":{}}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"webrtc","modality":"text"}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"websocket","modality":"video"}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"websocket","modality":false}""")]
    [InlineData("""{"agentId":"agent_1","channelType":"webrtc","apiVersion":"v1\r\nX-Other: value"}""")]
    public async Task InvalidOptionsFailBeforeHttp(string options)
    {
        var provider = Provider((_, _) => throw new Exception("Unexpected HTTP call"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(Request(options)));
    }

    [Theory]
    [InlineData("""{"companionId":"persona_1"}""")]
    [InlineData("""{"companionId":"persona_1","providerConfig":[]}""")]
    [InlineData("""{"companionId":"persona_1","providerConfig":{},"channelType":"websocket"}""")]
    public async Task InvalidCompanionOptionsFailBeforeHttp(string options)
    {
        var provider = Provider((_, _) => throw new Exception("Unexpected HTTP call"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(Request(options, "companion")));
    }

    [Fact]
    public async Task MissingCredentialsAndUnknownModelsFailBeforeHttp()
    {
        var provider = Provider((_, _) => throw new Exception("Unexpected HTTP call"), key: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(Request()));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(Request(model: "unknown")));
    }

    [Fact]
    public async Task MalformedServerCredentialIsNotIncludedInHeaderValidationErrors()
    {
        var provider = Provider((_, _) => throw new Exception("Unexpected HTTP call"), key: ServerKey + "\r\nInvalid");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(Request()));
        Assert.DoesNotContain(ServerKey, error.ToString());
    }

    [Fact]
    public async Task AgentIdIsEscapedAndCredentialsAreRequestScoped()
    {
        var calls = 0;
        var provider = Provider((message, _) =>
        {
            calls++;
            Assert.Equal("/public/agents/agent%2Fwith%3Fquery%23fragment/connections", message.RequestUri!.AbsolutePath);
            Assert.Empty(message.RequestUri.Query);
            Assert.Empty(message.RequestUri.Fragment);
            Assert.Equal(ServerKey, Assert.Single(message.Headers.GetValues("X-Api-Key")));
            return Task.FromResult(Json(Native()));
        });
        var request = Request("""{"agentId":"agent/with?query#fragment","channelType":"webrtc"}""");
        _ = await provider.GetRealtimeToken(request);
        _ = await provider.GetRealtimeToken(request);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpErrorsAreExplicitSanitizedAndNeverRetried(int status)
    {
        var calls = 0;
        var provider = Provider((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(ServerKey + " short-lived private-provider-error")
            });
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(Request()));
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain(ServerKey, error.ToString());
        Assert.DoesNotContain("short-lived", error.ToString());
        Assert.DoesNotContain("private-provider-error", error.ToString());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"token":"not-base64","connection":{"id":"connection-1"}}""")]
    [InlineData("""{"token":42,"connection":{"id":"connection-1"}}""")]
    [InlineData("""{"token":"//79","connection":{"id":"connection-1"}}""")]
    public async Task MalformedResponsesFailWithoutEchoingTheBody(string body)
    {
        var provider = Provider((_, _) => Task.FromResult(Json(body)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(Request()));
        Assert.DoesNotContain("not-base64", error.ToString());
        Assert.DoesNotContain("not-json", error.ToString());
    }

    [Theory]
    [InlineData("""{"expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"http://unsafe.test"},"expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"wss://user:password@unsafe.test"},"expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"wss://safe.test"},"expiresAt":"2000-01-01T00:00:00Z"}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"wss://safe.test"},"expiresAt":12}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"wss://safe.test"}}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"wss://safe.test"},"expiresAt":"unknown"}""")]
    [InlineData("""{"connection":{"id":"c","signalingEndpoint":"wss://safe.test"},"expiresAt":"2099-01-01T00:00:00Z","secret":"fixture-server-credential"}""")]
    public async Task InvalidTokenDetailsNeverFabricateExpiryOrConnectionData(string details)
    {
        var provider = Provider((_, _) => Task.FromResult(Json(Native(details))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(Request()));
        Assert.DoesNotContain(ServerKey, error.ToString());
        Assert.DoesNotContain("password", error.ToString());
    }

    [Fact]
    public async Task MissingConnectionIdFailsEvenWhenTokenIsValid()
    {
        var provider = Provider((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { token = Encode(RtcDetails), connection = new { } }))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(Request()));
    }

    [Theory]
    [InlineData("""{"url":"http://unsafe.test","expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"url":"https://safe.test/session?token=short#fragment","expiresAt":"2099-01-01T00:00:00Z"}""")]
    [InlineData("""{"expiresAt":"2099-01-01T00:00:00Z"}""")]
    public async Task InvalidWebsocketDetailsFailWithoutReturningAConnection(string details)
    {
        var provider = Provider((_, _) => Task.FromResult(Json(Native(details))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRealtimeToken(Request("""{"agentId":"agent_1","channelType":"websocket"}""")));
    }

    [Fact]
    public async Task CancellationBeforeAndDuringHttpIsPropagated()
    {
        var provider = Provider((_, _) => throw new Exception("Unexpected HTTP call"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRealtimeToken(Request(), cancelled.Token));

        using var cancellation = new CancellationTokenSource();
        provider = Provider(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new Exception("Unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRealtimeToken(Request(), cancellation.Token));
    }

    [Fact]
    public async Task CancellationWhileReadingResponseDisposesTheStream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellingStream(cancellation);
        var provider = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRealtimeToken(Request(), cancellation.Token));
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("""{"session":{"model":"napster/agent","agentId":"agent_1","channelType":"webrtc","instructions":"not-an-initial-greeting"},"transport":{"type":"napster"}}""")]
    [InlineData("""{"session":{"model":"napster/agent","agentId":"agent_1","channelType":"webrtc","store":false},"transport":{"type":"napster"}}""")]
    [InlineData("""{"session":{"model":"napster/agent","agentId":"agent_1","channelType":"webrtc"},"transport":{"type":"napster","sdp":"offer"}}""")]
    [InlineData("""{"session":{"model":"napster/agent"},"transport":{"type":"webrtc","sdp":"offer"}}""")]
    [InlineData("""{"session":{"model":"napster/agent","agentId":"agent_1","channelType":"kiosk"},"transport":{"type":"napster"}}""")]
    public async Task LiveSettingsAreNeverSilentlyDiscarded(string wire)
    {
        var provider = Provider((_, _) => throw new Exception("Unexpected HTTP call"));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateLiveSessionAsync(JsonSerializer.Deserialize<LiveSessionRequest>(wire)!));
    }

    private static NapsterProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, string? key = ServerKey)
        => new(new Keys(key), new Factory(new HttpClient(new Handler(respond))));

    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) { Assert.Equal("napster", provider); return key; }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.Equal("napster-api", name); return client; }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
