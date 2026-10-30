using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.KalpaLabs;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    private const string SpeechTool = "generate_speech";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var speechRequest = UnifiedSpeechRequest(request);
        var plan = PrepareSpeech(speechRequest, streaming: false);
        var body = await SendJsonAsync(HttpMethod.Post, plan.Path, plan.Payload, cancellationToken);
        var text = body.TryGetProperty("reply", out var reply) ? reply.GetProperty("text").GetString() : null;
        var content = new List<AIContentPart>();
        if (text is not null)
            content.Add(new AITextContentPart { Type = "text", Text = text });
        var audio = reply.ValueKind == JsonValueKind.Object ? reply.GetProperty("audio") : body.GetProperty("audio");
        if (audio.ValueKind != JsonValueKind.Null)
        {
            var base64 = audio.GetProperty("data_b64").GetString()!;
            ValidateAudio(base64, pcm: false);
            if (audio.GetProperty("format").GetString() != "wav")
                throw new InvalidOperationException("KalpaLabs returned an unexpected audio encoding.");
            content.Add(new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = ToolId(request), ToolName = SpeechTool,
                Title = "Generate speech", Input = plan.Payload, State = "output-available",
                ProviderExecuted = true,
                Output = AudioResult(base64, "audio/wav", body, completed: true,
                    plan.Warnings.Concat(UnifiedWarnings(request))),
                Metadata = new() { [ProviderId] = body }
            });
        }
        return new AIResponse
        {
            ProviderId = ProviderId, Model = request.Model, Status = "completed",
            Output = new() { Items = [new() { Role = "assistant", Content = content }] },
            Metadata = new() { [ProviderId] = new { response = body, warnings = plan.Warnings.Concat(UnifiedWarnings(request)) } }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var plan = PrepareSpeech(UnifiedSpeechRequest(request), streaming: true);
        plan.Warnings.AddRange(UnifiedWarnings(request));
        cancellationToken.ThrowIfCancellationRequested();
        var id = ToolId(request);
        yield return UnifiedEvent("tool-input-available", id, new AIToolInputAvailableEventData
        {
            ToolName = SpeechTool, Title = "Generate speech", Input = plan.Payload, ProviderExecuted = true,
            ProviderMetadata = new() { [ProviderId] = new() { ["warnings"] = plan.Warnings } }
        });
        await foreach (var chunk in StreamSpeech(plan, cancellationToken))
        {
            if (chunk.Audio is not null)
            {
                // Forward the actual PCM chunk, not a progress marker or an accumulated clip.
                yield return UnifiedEvent("tool-output-available", id, new AIToolOutputAvailableEventData
                {
                    ToolName = SpeechTool, ProviderExecuted = true, Preliminary = true,
                    Output = AudioResult(chunk.Audio, "audio/pcm", JsonSerializer.SerializeToElement(new
                    {
                        sample_rate = chunk.Rate, format = "pcm_s16le", channels = 1
                    }), completed: false)
                });
            }
            if (chunk.Kind != "end")
                continue;
            if (chunk.Data.TryGetProperty("text", out var text) && text.GetString() is { } reply)
            {
                yield return UnifiedEvent("text-start", id + "_text", new AITextStartEventData());
                cancellationToken.ThrowIfCancellationRequested();
                yield return UnifiedEvent("text-delta", id + "_text", new AITextDeltaEventData { Delta = reply });
                cancellationToken.ThrowIfCancellationRequested();
                yield return UnifiedEvent("text-end", id + "_text", new AITextEndEventData());
            }
            cancellationToken.ThrowIfCancellationRequested();
            yield return UnifiedEvent("tool-output-available", id, new AIToolOutputAvailableEventData
            {
                ToolName = SpeechTool, ProviderExecuted = true, Preliminary = false,
                Output = AudioResult(null, "audio/pcm", chunk.Data, completed: true, plan.Warnings),
                ProviderMetadata = new() { [ProviderId] = new() { ["response"] = chunk.Data } }
            });
            cancellationToken.ThrowIfCancellationRequested();
            yield return UnifiedEvent("finish", id, new AIFinishEventData
            {
                FinishReason = "stop", Model = request.Model,
                MessageMetadata = AIFinishMessageMetadata.FromDictionary(new()
                {
                    [ProviderId] = chunk.Data
                }, request.Model)
            });
        }
    }

    private static SpeechRequest UnifiedSpeechRequest(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tools?.Count > 0 || HasValue(request.ToolChoice) ||
            HasValue(request.ResponseFormat) || !string.IsNullOrWhiteSpace(request.Instructions))
            throw new NotSupportedException("KalpaLabs converse does not support tools, response schemas, or system instructions.");
        var providerOptions = UnifiedOptions(request.Metadata);
        var options = ReadOptions(providerOptions);
        var (_, modelVoice) = ParseModel(request.Model!);
        var items = request.Input?.Items ?? [];
        var text = request.Input?.Text;
        if (options.Conversation is null && modelVoice is null && options.Voice is null)
        {
            var turns = new List<KalpaLabsTurn>();
            foreach (var item in items)
            {
                if (item.Type != "message" || item.Role is not ("user" or "assistant"))
                    throw new NotSupportedException("KalpaLabs conversation messages must be user or assistant turns; use the conversation provider option for native speaker labels.");
                var parts = item.Content ?? [];
                if (parts.Any(p => p is not AITextContentPart and not AIFileContentPart &&
                    p is not AIToolCallContentPart { ToolName: SpeechTool, ProviderExecuted: true }))
                    throw new NotSupportedException("KalpaLabs conversation turns support text and PCM WAV audio only.");
                var files = parts.OfType<AIFileContentPart>().ToArray();
                if (files.Length > 1)
                    throw new NotSupportedException("KalpaLabs accepts one WAV audio clip per conversation turn.");
                var turnText = string.Join("\n", parts.OfType<AITextContentPart>().Select(p => p.Text));
                turns.Add(new()
                {
                    Speaker = item.Role == "assistant" ? "1" : "0",
                    Text = turnText.Length == 0 ? null : turnText,
                    AudioWavBase64 = files.Length == 0
                        ? parts.OfType<AIToolCallContentPart>().Select(ReplayWav).FirstOrDefault(a => a is not null)
                        : WavInput(files[0])
                });
            }

            if (turns.Count == 0 && text is not null)
                turns.Add(new() { Text = text });
            if (turns.Count == 0)
                throw new ArgumentException("KalpaLabs requires conversation input.");
            turns.Add(new() { Speaker = options.Speaker ?? "1" });
            options.Conversation = turns;
        }
        else if (options.Conversation is null)
        {
            var lastUser = items.LastOrDefault(i => i.Role == "user");
            if (lastUser is not null)
                text = string.Join("\n", lastUser.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        }
        if (request.Temperature is not null)
        {
            options.Params ??= new();
            options.Params.Temperature ??= request.Temperature;
        }
        return new SpeechRequest
        {
            Model = request.Model!, Text = text!,
            ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(options, Json) }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamImmutableSpeechToolsAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
        {
            if (part.Event.Data is not AIToolOutputAvailableEventData { Preliminary: true } audio)
            {
                yield return part;
                continue;
            }
            // Messages/Responses tool outputs are immutable. Finalize each PCM chunk as
            // its own provider-executed result instead of losing or buffering updates.
            var id = $"{part.Event.Id}_chunk_{index++}";
            yield return UnifiedEvent("tool-input-available", id, new AIToolInputAvailableEventData
            {
                ToolName = SpeechTool, ProviderExecuted = true,
                Input = new { streamId = part.Event.Id, chunk = index - 1 }
            });
            cancellationToken.ThrowIfCancellationRequested();
            yield return UnifiedEvent("tool-output-available", id, new AIToolOutputAvailableEventData
            {
                ToolName = SpeechTool, ProviderExecuted = true, Preliminary = false, Output = audio.Output
            });
        }
    }

    private static string WavInput(AIFileContentPart file)
    {
        // The chat-completions mapper preserves input_audio as its native JSON part.
        if (file.Data is JsonElement { ValueKind: JsonValueKind.Object } native &&
            native.TryGetProperty("input_audio", out var inputAudio))
        {
            if (inputAudio.GetProperty("format").GetString() != "wav")
                throw new NotSupportedException("KalpaLabs conversation input audio must be PCM WAV.");
            var base64 = inputAudio.GetProperty("data").GetString()!;
            ValidateAudio(base64, pcm: false);
            return base64;
        }
        if (file.MediaType is not ("audio/wav" or "audio/x-wav"))
            throw new NotSupportedException("KalpaLabs conversation input audio must be a base64 PCM WAV, not a URL or another encoding.");
        var value = file.Data switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            string data => data,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new ArgumentException("KalpaLabs WAV input requires base64 or bytes.")
        };
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new ArgumentException("KalpaLabs WAV input requires a base64 data URL.");
            value = value[(marker + 8)..];
        }
        ValidateAudio(value, pcm: false);
        return value;
    }

    private static string? ReplayWav(AIToolCallContentPart tool)
    {
        if (tool.Output is null)
            return null;
        var output = JsonSerializer.SerializeToElement(tool.Output, Json);
        if (output.ValueKind != JsonValueKind.Object || !output.TryGetProperty("structuredContent", out var structured) ||
            !structured.TryGetProperty("response", out var response))
            return null;
        var audio = response.TryGetProperty("reply", out var reply)
            ? reply.GetProperty("audio")
            : response.TryGetProperty("audio", out var ttsAudio) ? ttsAudio : default;
        if (audio.ValueKind != JsonValueKind.Object || audio.GetProperty("format").GetString() != "wav")
            return null;
        var base64 = audio.GetProperty("data_b64").GetString()!;
        ValidateAudio(base64, pcm: false);
        return base64;
    }

    private static Dictionary<string, JsonElement>? UnifiedOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return null;
        var root = JsonSerializer.SerializeToElement(metadata, Json);
        if (root.TryGetProperty(ProviderId, out var direct))
            return new() { [ProviderId] = direct };
        foreach (var key in new[] { "providerOptions", "chatcompletions.request.unmapped", "chatcompletions.request.metadata",
            "messages.request.metadata", "messages.request.unmapped" })
        {
            if (!root.TryGetProperty(key, out var candidate) || candidate.ValueKind != JsonValueKind.Object)
                continue;
            if (candidate.TryGetProperty(ProviderId, out var options))
                return new() { [ProviderId] = options };
            if (candidate.TryGetProperty("providerOptions", out var wrapped))
                return wrapped.Deserialize<Dictionary<string, JsonElement>>(Json);
        }
        return null;
    }

    private static bool HasValue(object? value)
        => value is not null and not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static IEnumerable<object> UnifiedWarnings(AIRequest request)
    {
        if (request.MaxOutputTokens is not null)
            yield return new { type = "ignored", feature = "maxOutputTokens", reason = "KalpaLabs has no token limit parameter." };
        if (request.TopP is not null)
            yield return new { type = "ignored", feature = "topP", reason = "KalpaLabs has no top_p parameter." };
    }

    private static CallToolResult AudioResult(string? base64, string mimeType, JsonElement metadata, bool completed, IEnumerable<object>? warnings = null)
        => new()
        {
            IsError = false,
            Content = base64 is null ? [] :
            [
                new EmbeddedResourceBlock
                {
                    Resource = new BlobResourceContents
                    {
                        Uri = "audio://generated/speech", MimeType = mimeType,
                        Blob = Encoding.UTF8.GetBytes(base64)
                    }
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                status = completed ? "completed" : "streaming", mediaType = mimeType,
                response = metadata, warnings
            }, Json)
        };

    private static string ToolId(AIRequest request) => $"speech_{request.Id ?? Guid.NewGuid().ToString("N")}";

    private static AIStreamEvent UnifiedEvent(string type, string id, object data) => new()
    {
        ProviderId = ProviderId,
        Event = new() { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data }
    };
}
