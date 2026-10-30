using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.FishAudio;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.FishAudio;

public sealed class FishAudioModernizationTests
{
    private const string Session = """{"session_id":"session-1","transport":"livekit","livekit_url":"wss://room.example.test","token":"ephemeral-fixture","expires_at":"2099-01-01T00:00:00Z","max_duration_seconds":1800}""";
    private static Dictionary<string, JsonElement> Options(string json) => new() { ["fishaudio"] = JsonSerializer.Deserialize<JsonElement>(json) };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static StreamingSpeechRequest Speech(string format = "mp3") => new() { Model = "fishaudio/s2.1-pro-free", Text = [new() { Text = "Hel" }, new() { Text = "lo" }], OutputFormat = new() { Type = format } };
    private static async Task<List<StreamingSpeechPart>> Collect(FishAudioProvider provider, StreamingSpeechRequest request, CancellationToken ct = default)
    {
        var result = new List<StreamingSpeechPart>();
        await foreach (var part in provider.SpeechStreamingAsync(request, ct)) result.Add(part);
        return result;
    }

    [Fact]
    public async Task NativeSseDecodesOrderedAudioAndReplacesAlignmentSnapshots()
    {
        JsonElement payload = default;
        var sse = ": keepalive\r\nevent: message\r\ndata: {\"audio_base64\":\"AAE=\",\"content\":\"Hello\",\"alignment\":null,\"chunk_seq\":0,\"chunk_audio_offset_sec\":0}\r\n\r\n"
            + "data: {\"audio_base64\":\"AgM=\",\n"
            + "data: \"alignment\":{\"audio_duration\":1,\"segments\":[{\"text\":\"old\",\"start\":0,\"end\":1}]},\"chunk_seq\":0,\"chunk_audio_offset_sec\":2}\n\n"
            + "data: {\"audio_base64\":\"BAU=\",\"alignment\":{\"audio_duration\":2,\"segments\":[{\"text\":\"new\",\"start\":0,\"end\":2}]},\"chunk_seq\":0,\"chunk_audio_offset_sec\":2}\n\n";
        var fragmented = new FragmentedStream(Encoding.UTF8.GetBytes(sse));
        var provider = Provider(request =>
        {
            Assert.Equal("/v1/tts/stream/with-timestamp", request.RequestUri!.AbsolutePath);
            Assert.Equal("s2.1-pro-free", request.Headers.GetValues("model").Single());
            payload = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().Result);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(fragmented) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        var request = Speech("audio/pcm"); request.OutputFormat!.Rate = 24000; request.IncludeRawChunks = true;
        request.Language = "es-MX"; request.Speed = 1.2;
        request.ProviderOptions = Options("""{"temperature":0.8,"prosody":{"volume":3,"normalize_loudness":false},"features":["quality-guard"]}""");
        var parts = await Collect(provider, request);
        Assert.IsType<SpeechStreamStartPart>(parts[0]); Assert.IsType<SpeechResponseMetadataPart>(parts[1]);
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("es-MX", payload.GetProperty("language").GetString());
        Assert.Equal(3, payload.GetProperty("prosody").GetProperty("volume").GetInt32());
        Assert.False(payload.GetProperty("prosody").GetProperty("normalize_loudness").GetBoolean());
        Assert.Equal(3, parts.OfType<SpeechRawPart>().Count());
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, parts.OfType<SpeechAudioPart>().SelectMany(a => Convert.FromBase64String(a.Audio)).ToArray());
        Assert.All(parts.OfType<SpeechAudioPart>(), a => { Assert.Equal("audio/pcm", a.AudioFormat.Type); Assert.Equal(24000, a.AudioFormat.Rate); });
        var final = Assert.Single(parts.OfType<SpeechFinishPart>()).ProviderMetadata!["fishaudio"].GetProperty("alignments");
        Assert.Equal(1, final.GetArrayLength()); Assert.Equal("new", final[0].GetProperty("alignment").GetProperty("segments")[0].GetProperty("text").GetString());
        Assert.True(fragmented.Disposed);
    }

    [Theory]
    [InlineData("mp3", "audio/mpeg", 44100)]
    [InlineData("wav", "audio/wav", 44100)]
    [InlineData("opus", "audio/ogg", 48000)]
    public async Task FormatsUseAudioMetadataNotTheSseContentType(string format, string mime, int rate)
    {
        var provider = Provider(_ => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"audio_base64\":\"AAE=\"}\n\n", Encoding.UTF8, "text/event-stream") });
        var audio = Assert.Single((await Collect(provider, Speech(format))).OfType<SpeechAudioPart>());
        Assert.Equal(mime, audio.AudioFormat.Type); Assert.Equal(rate, audio.AudioFormat.Rate);
    }

    [Theory]
    [InlineData("{\"audio_base64\":\"!invalid\"}")]
    [InlineData("{\"error\":\"failed\"}")]
    [InlineData("{\"content\":\"no audio\"}")]
    [InlineData("not-json")]
    public async Task StreamFailuresNeverProduceFinish(string data)
    {
        var provider = Provider(_ => new(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\n", Encoding.UTF8, "text/event-stream") });
        var parts = new List<StreamingSpeechPart>();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var part in provider.SpeechStreamingAsync(Speech())) parts.Add(part); });
        Assert.Empty(parts.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task CancellationAndConflictingPartOptionsDoNotCallUpstream()
    {
        var provider = Provider(_ => throw new Exception("Unexpected HTTP call"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider, Speech(), cancelled.Token));
        var request = Speech(); request.Text[1].Voice = "different";
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider, request));
        request = Speech("flac"); await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider, request));
        request = Speech("opus"); request.OutputFormat!.Rate = 44100;
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(provider, request));
        request = Speech(); request.ProviderOptions = Options("""{"references":[{"audio":"AAE=","text":"test"}]}""");
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(provider, request));
    }

    [Fact]
    public async Task AudioIsProgressiveAndCancellationDisposesTheUpstreamWithoutFinish()
    {
        var sse = string.Concat(Enumerable.Repeat("data: {\"audio_base64\":\"AAE=\"}\n\n", 100));
        var stream = new FragmentedStream(Encoding.UTF8.GetBytes(sse));
        var provider = Provider(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        using var cancellation = new CancellationTokenSource();
        await using (var parts = provider.SpeechStreamingAsync(Speech(), cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await parts.MoveNextAsync()); Assert.IsType<SpeechStreamStartPart>(parts.Current);
            Assert.True(await parts.MoveNextAsync()); Assert.IsType<SpeechResponseMetadataPart>(parts.Current);
            Assert.True(await parts.MoveNextAsync()); Assert.IsType<SpeechAudioPart>(parts.Current);
            Assert.True(stream.Position < stream.Length); // No buffered full-generation fallback.
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await parts.MoveNextAsync());
        }
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SessionCreationPreservesSdkTokenAndLiveKitContract()
    {
        var provider = Provider(request =>
        {
            Assert.Equal("/v1/agent/sessions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var body = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("agent-1", body.GetProperty("agent_id").GetString());
            Assert.Equal("nl", body.GetProperty("overrides").GetProperty("language").GetString());
            return Json(Session);
        });
        var options = Options("""{"overrides":{"language":"nl"},"record_audio":false,"tool_events":false}""");
        var token = await provider.GetRealtimeToken(new RealtimeRequest { Model = "fishaudio/agents/agent-1", ProviderOptions = options });
        Assert.Equal("ephemeral-fixture", token.Value); Assert.Equal(4070908800, token.ExpiresAt);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Session), token.ProviderMetadata!["fishaudio"]));
        var live = await provider.CreateLiveSessionAsync(new() { Session = new() { Model = "fishaudio/agents/agent-1",
            AdditionalProperties = options["fishaudio"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) }, Transport = new LiveAdditionalRequestTransport("livekit") });
        var wire = JsonSerializer.Serialize(live);
        var roundtrip = JsonSerializer.Deserialize<LiveSessionResponse>(wire)!;
        var transport = Assert.IsType<LiveAdditionalResponseTransport>(roundtrip.Transport);
        Assert.Equal("wss://room.example.test", transport.AdditionalProperties!["livekit_url"].GetString());
        Assert.Equal(token.Value, transport.AdditionalProperties["token"].GetString()); Assert.Equal(1800, transport.AdditionalProperties["max_duration_seconds"].GetInt32());
        Assert.Equal("session-1", live.Session.Id);
        Assert.IsType<LiveAdditionalRequestTransport>(JsonSerializer.Deserialize<LiveSessionRequest>("""{"session":{"model":"fishaudio/agents/agent-1"},"transport":{"type":"livekit"}}""")!.Transport);
    }

    [Theory]
    [InlineData("transport", "other")]
    [InlineData("livekit_url", "http://unsafe.test")]
    [InlineData("token", "")]
    [InlineData("session_id", "")]
    [InlineData("expires_at", "2000-01-01T00:00:00Z")]
    public async Task InvalidSessionResponsesFailWithoutLeakingTokens(string field, string value)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(Session)!; body[field] = value;
        var provider = Provider(_ => Json(body.ToJsonString()));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.GetRealtimeToken(new() { Model = "agents/agent-1" }));
        Assert.DoesNotContain("ephemeral-fixture", error.ToString());
    }

    [Fact]
    public void LiveExtensionsDoNotAddProviderFieldsToOpenAiWireContracts()
    {
        const string native = """{"session":{"model":"openai/live"},"transport":{"type":"webrtc","sdp":"offer"}}""";
        var request = JsonSerializer.Deserialize<LiveSessionRequest>(native)!;
        Assert.IsType<LiveWebRtcRequestTransport>(request.Transport);
        var wire = JsonSerializer.Serialize(request);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(native), JsonSerializer.Deserialize<JsonElement>(wire)));
        Assert.DoesNotContain("providerOptions", wire); Assert.DoesNotContain("additionalProperties", wire);
        var extended = JsonSerializer.Deserialize<LiveSessionRequest>("""{"session":{"model":"fishaudio/agents/fixture","record_audio":false,"overrides":{"language":"nl"}},"transport":{"type":"livekit","future_option":true}}""")!;
        Assert.False(extended.Session.AdditionalProperties!["record_audio"].GetBoolean());
        Assert.True(Assert.IsType<LiveAdditionalRequestTransport>(extended.Transport).AdditionalProperties!["future_option"].GetBoolean());
        Assert.IsType<LiveSipResponseTransport>(JsonSerializer.Deserialize<LiveSessionResponse>("""{"session":{"id":"sip"},"transport":{"type":"sip"}}""")!.Transport);
    }

    [Fact]
    public async Task AgentDiscoveryPaginatesEscapesCursorsAndPreservesCatalog()
    {
        var calls = 0;
        var provider = Provider(request =>
        {
            Assert.DoesNotContain("page=", request.RequestUri!.Query);
            if (++calls == 2) Assert.Contains("cursor=a%2Bb%2F%3D", request.RequestUri.Query);
            var id = calls.ToString();
            return Json("{\"agents\":[{\"agent_id\":\"agent-" + id + "\",\"name\":\"Agent\",\"status\":\"active\",\"publication_state\":\"live\",\"workspace_id\":\"workspace\",\"public_enabled\":true,\"overrides_allowed\":[\"voice_id\"]},"
                + "{\"agent_id\":\"draft\",\"status\":\"active\",\"publication_state\":\"draft\"}],\"has_more\":" + (calls == 1 ? "true" : "false") + ",\"next_cursor\":\"a+b/=\"}");
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(2, calls); Assert.Contains(models, m => m.Id == "fishaudio/s2.1-pro");
        Assert.Equal(2, models.Count(m => m.Id.StartsWith("fishaudio/agents/")));
        Assert.DoesNotContain(models, m => m.Id.EndsWith("/draft"));
        Assert.Contains("workspace_id:workspace", models.Single(m => m.Id == "fishaudio/agents/agent-1").Tags!);
    }

    [Fact]
    public async Task AgentCursorLoopFailsAndOptionalDiscoveryFailureKeepsSpeech()
    {
        var provider = Provider(_ => Json("""{"agents":[],"has_more":true,"next_cursor":"loop"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());
        provider = Provider(_ => new(HttpStatusCode.Forbidden));
        Assert.Contains(await provider.ListModels(), m => m.Id == "fishaudio/s2.1-pro");
    }

    private static FishAudioProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Keys(), new Factory(new HttpClient(new Handler(respond))));
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "fixture-server-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
