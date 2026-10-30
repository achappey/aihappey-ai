using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.EdgeSpeak;

public sealed partial class EdgeSpeakProvider
{
    private async Task<AIResponse> ExecuteSpeechUnified(AIRequest request, CancellationToken ct)
    {
        var speech = UnifiedSpeech(request);
        var response = await SpeechRequest(speech, ct);
        return new()
        {
            ProviderId = ProviderId, Model = request.Model, Status = "completed",
            Output = new() { Items = [new()
            {
                Role = "assistant", Content = [new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = $"speech_{request.Id ?? Guid.NewGuid().ToString("N")}",
                    ToolName = "generate_speech", Title = "Generate speech", State = "output-available",
                    ProviderExecuted = true, Input = response.Request?.Body,
                    Output = AudioResult(response.Audio.Base64, response.Audio.MimeType, completed: true)
                }]
            }] }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamSpeechUnified(
        AIRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var plan = PrepareSpeech(UnifiedSpeech(request), streaming: true);
        var id = $"speech_{request.Id ?? Guid.NewGuid().ToString("N")}";
        ct.ThrowIfCancellationRequested();
        yield return UnifiedEvent("tool-input-available", id, new AIToolInputAvailableEventData
        {
            ToolName = "generate_speech", Title = "Generate speech", Input = plan.Body, ProviderExecuted = true
        });
        await foreach (var item in StreamSpeech(plan, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type == "speech.audio.delta")
                yield return UnifiedEvent("tool-output-available", id, new AIToolOutputAvailableEventData
                {
                    ToolName = "generate_speech", ProviderExecuted = true, Preliminary = true,
                    Output = AudioResult(SpeechAudio(item.Data), "audio/wav", completed: false)
                });
            else if (item.Type == "speech.audio.done")
            {
                yield return UnifiedEvent("tool-output-available", id, new AIToolOutputAvailableEventData
                {
                    ToolName = "generate_speech", ProviderExecuted = true, Preliminary = false,
                    Output = AudioResult(null, "audio/wav", completed: true),
                    ProviderMetadata = new() { [ProviderId] = new() { ["response"] = item.Data } }
                });
                ct.ThrowIfCancellationRequested();
                yield return UnifiedEvent("finish", id, new AIFinishEventData { FinishReason = "stop", Model = request.Model });
            }
        }
    }

    private static SpeechRequest UnifiedSpeech(AIRequest request)
    {
        var options = UnifiedOptions(request);
        var metadata = JsonSerializer.SerializeToElement(request.Metadata ?? []);
        foreach (var name in new[] { "voice", "response_format", "speed", "language" })
            if (metadata.TryGetProperty(name, out var value) && !options.ContainsKey(name))
                options[name] = value.Clone();
        if (metadata.TryGetProperty("outputFormat", out var format) && !options.ContainsKey("response_format"))
            options["response_format"] = format.Clone();
        if (request.Temperature is not null && !options.ContainsKey("temperature"))
            options["temperature"] = JsonSerializer.SerializeToElement(request.Temperature);
        if (request.TopP is not null && !options.ContainsKey("top_p"))
            options["top_p"] = JsonSerializer.SerializeToElement(request.TopP);
        var lastUser = request.Input?.Items?.LastOrDefault(i => i.Role == "user");
        var text = lastUser is null ? request.Input?.Text :
            string.Join("\n", lastUser.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        return new()
        {
            Model = request.Model!, Text = text!, Instructions = request.Instructions,
            ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(options) }
        };
    }

    private async Task<AIResponse> ExecuteTranscriptionUnified(AIRequest request, CancellationToken ct)
    {
        var response = await TranscriptionRequest(UnifiedTranscription(request), ct);
        return new()
        {
            ProviderId = ProviderId, Model = request.Model, Status = "completed",
            Metadata = new() { [ProviderId] = response.ProviderMetadata![ProviderId] },
            Output = new() { Items = [new()
            {
                Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = response.Text }]
            }] }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamTranscriptionUnified(
        AIRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var transcription = UnifiedTranscription(request);
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        var sawDelta = false;
        ct.ThrowIfCancellationRequested();
        yield return UnifiedEvent("text-start", id, new AITextStartEventData());
        await foreach (var item in StreamTranscription(transcription, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type == "transcript.text.delta")
            {
                sawDelta = true;
                yield return UnifiedEvent("text-delta", id, new AITextDeltaEventData { Delta = RequiredString(item.Data, "delta") });
            }
            else if (item.Type == "transcript.text.done")
            {
                if (!sawDelta)
                    yield return UnifiedEvent("text-delta", id, new AITextDeltaEventData { Delta = RequiredString(item.Data, "text") });
                ct.ThrowIfCancellationRequested();
                yield return UnifiedEvent("text-end", id, new AITextEndEventData());
                ct.ThrowIfCancellationRequested();
                yield return UnifiedEvent("finish", id, new AIFinishEventData
                {
                    FinishReason = "stop", Model = request.Model,
                    MessageMetadata = AIFinishMessageMetadata.FromDictionary(new() { [ProviderId] = item.Data }, request.Model)
                });
            }
        }
    }

    private static TranscriptionRequest UnifiedTranscription(AIRequest request)
    {
        var file = request.Input?.Items?.LastOrDefault(i => i.Role == "user")?.Content?
            .OfType<AIFileContentPart>().LastOrDefault(f =>
                f.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
                f.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new ArgumentException("EdgeSpeak transcription requires audio in the last user message.");
        var audio = file.Data switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new ArgumentException("EdgeSpeak transcription requires base64 audio or bytes, not a URL.")
        };
        var options = UnifiedOptions(request);
        if (request.Instructions is not null && !options.ContainsKey("prompt"))
            options["prompt"] = JsonSerializer.SerializeToElement(request.Instructions);
        return new()
        {
            Model = request.Model!, Audio = audio, MediaType = file.MediaType!,
            ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(options) }
        };
    }

    private static Dictionary<string, JsonElement> UnifiedOptions(AIRequest request)
    {
        var metadata = JsonSerializer.SerializeToElement(request.Metadata ?? []);
        if (metadata.TryGetProperty(ProviderId, out var direct))
            return NativeOptions(new() { [ProviderId] = direct });
        if (metadata.TryGetProperty("providerOptions", out var wrapper) &&
            wrapper.ValueKind == JsonValueKind.Object && wrapper.TryGetProperty(ProviderId, out var native))
            return NativeOptions(new() { [ProviderId] = native });
        return new();
    }

    private static CallToolResult AudioResult(string? audio, string mime, bool completed) => new()
    {
        IsError = false,
        Content = audio is null ? [] : [new EmbeddedResourceBlock
        {
            Resource = new BlobResourceContents
            {
                Uri = "audio://generated/speech", MimeType = mime, Blob = Encoding.UTF8.GetBytes(audio)
            }
        }],
        StructuredContent = JsonSerializer.SerializeToElement(new { status = completed ? "completed" : "streaming", mediaType = mime })
    };

    private static AIStreamEvent UnifiedEvent(string type, string id, object data) => new()
    {
        ProviderId = ProviderId,
        Event = new() { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data }
    };
}
