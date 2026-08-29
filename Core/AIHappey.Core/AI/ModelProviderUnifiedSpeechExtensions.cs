using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

/// <summary>
/// Adapts provider speech operations to the unified conversation contract as
/// a synthetic, provider-executed tool call.
/// </summary>
public static class ModelProviderUnifiedSpeechExtensions
{
    private const string ToolName = "generate_speech";

    public static async Task<bool> IsSpeechModelAsync(
        this IModelProvider modelProvider,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var model = await modelProvider.GetModel(modelId, cancellationToken);
        return string.Equals(model.Type, "speech", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AIResponse> ExecuteUnifiedSpeechAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = modelProvider.GetIdentifier();
        var speechRequest = CreateSpeechRequest(request, providerId);
        var toolCallId = CreateToolCallId(request);
        var response = await modelProvider.SpeechRequest(speechRequest, cancellationToken);
        var (audio, mimeType) = GetSpeechAudio(response);

        return new AIResponse
        {
            ProviderId = providerId,
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-call",
                                ToolCallId = toolCallId,
                                ToolName = ToolName,
                                Title = "Generate speech",
                                Input = CreateToolInput(speechRequest),
                                State = "output-available",
                                Output = CreateAudioCallToolResult(audio, mimeType, response.Warnings),
                                ProviderExecuted = true,
                                Metadata = CreateToolMetadata(providerId, response.ProviderMetadata)
                            }
                        ]
                    }
                ]
            }
        };
    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedSpeechAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = modelProvider.GetIdentifier();
        var speechRequest = CreateOpenAISpeechRequest(request, providerId);
        var toolCallId = CreateToolCallId(request);

        yield return CreateEvent(providerId, "tool-input-available", toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = ToolName,
                Title = "Generate speech",
                Input = CreateToolInput(speechRequest),
                ProviderExecuted = true
            });

        using var audio = new MemoryStream();
        var chunkCount = 0;
        var completed = false;
        AudioSpeechUsage? usage = null;
        Exception? streamError = null;

        await using var enumerator = modelProvider
            .OpenAISpeechStreamingAsync(speechRequest, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (streamError is null)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                streamError = ex;
                break;
            }

            if (!hasNext)
                break;

            var streamEvent = enumerator.Current;
            if (streamEvent is AudioSpeechStreamDelta delta)
            {
                byte[]? bytes = null;
                try
                {
                    if (completed)
                        throw new InvalidOperationException("The speech stream returned audio data after completion.");
                    bytes = DecodeAudioDelta(delta.Audio);
                }
                catch (Exception ex)
                {
                    streamError = ex;
                    continue;
                }

                await audio.WriteAsync(bytes, cancellationToken);
                chunkCount++;

                yield return CreateEvent(providerId, "tool-output-available", toolCallId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = ToolName,
                        Output = CreateProgressCallToolResult(chunkCount, audio.Length),
                        ProviderExecuted = true,
                        Preliminary = true
                    });
                continue;
            }

            if (streamEvent is AudioSpeechStreamDone done)
            {
                if (completed)
                    streamError = new InvalidOperationException("The speech stream returned more than one completion event.");
                else
                {
                    completed = true;
                    usage = done.Usage;
                }
                continue;
            }

            streamError = new InvalidOperationException(
                $"Unsupported speech stream event '{streamEvent?.GetType().Name ?? "null"}'.");
        }

        if (streamError is null && !completed)
            streamError = new InvalidOperationException("The speech stream ended without a completion event.");
        if (streamError is null && audio.Length == 0)
            streamError = new InvalidOperationException("The speech stream completed without audio data.");

        if (streamError is not null)
        {
            yield return CreateEvent(providerId, "tool-output-error", toolCallId,
                new AIToolOutputErrorEventData
                {
                    ToolCallId = toolCallId,
                    ErrorText = streamError.Message,
                    ProviderExecuted = true
                });
            yield return CreateFinishEvent(providerId, request.Model, toolCallId, "error");
            yield break;
        }

        yield return CreateEvent(providerId, "tool-output-available", toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = ToolName,
                Output = CreateAudioCallToolResult(
                    audio.ToArray(),
                    ResolveMimeType(speechRequest.ResponseFormat),
                    usage: usage),
                ProviderExecuted = true,
                Preliminary = false
            });
        yield return CreateFinishEvent(providerId, request.Model, toolCallId, "tool-calls");
    }

    private static SpeechRequest CreateSpeechRequest(AIRequest request, string providerId)
    {
        var openAIRequest = CreateOpenAISpeechRequest(request, providerId);
        return new SpeechRequest
        {
            Model = openAIRequest.Model,
            Text = openAIRequest.Input,
            Voice = openAIRequest.Voice,
            OutputFormat = openAIRequest.ResponseFormat,
            Instructions = openAIRequest.Instructions,
            Speed = openAIRequest.Speed,
            ProviderOptions = ToProviderOptions(request.Metadata)
        };
    }

    private static AudioSpeechRequest CreateOpenAISpeechRequest(AIRequest request, string providerId)
    {
        var model = request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var lastUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = string.Join("\n", lastUserMessage?.Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);

        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Speech conversation requests require text in the last user message.", nameof(request));

        return new AudioSpeechRequest
        {
            Model = GetProviderModelId(model, providerId),
            Input = text,
            Instructions = request.Instructions,
            Voice = ReadMetadataString(request.Metadata, "voice"),
            ResponseFormat = ReadMetadataString(request.Metadata, "response_format")
                ?? ReadMetadataString(request.Metadata, "outputFormat")
                ?? "mp3",
            Speed = ReadMetadataFloat(request.Metadata, "speed"),
            AdditionalProperties = ToProviderOptions(request.Metadata)
        };
    }

    private static (byte[] Audio, string MimeType) GetSpeechAudio(SpeechResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(response.Audio);

        var bytes = DecodeBase64Audio(response.Audio.Base64, "The speech response contained invalid base64 audio data.");
        if (bytes.Length == 0)
            throw new InvalidOperationException("The speech response contained no audio data.");

        var mimeType = string.IsNullOrWhiteSpace(response.Audio.MimeType)
            ? ResolveMimeType(response.Audio.Format)
            : response.Audio.MimeType;
        return (bytes, mimeType);
    }

    private static byte[] DecodeAudioDelta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("The speech stream returned an empty audio delta.");

        var bytes = DecodeBase64Audio(value, "The speech stream returned malformed base64 audio data.");
        if (bytes.Length == 0)
            throw new InvalidOperationException("The speech stream returned an empty audio delta.");
        return bytes;
    }

    private static byte[] DecodeBase64Audio(string value, string error)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new InvalidOperationException(error);
            value = value[(marker + ";base64,".Length)..];
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(error, ex);
        }
    }

    private static CallToolResult CreateAudioCallToolResult(
        byte[] audio,
        string mimeType,
        IEnumerable<object>? warnings = null,
        AudioSpeechUsage? usage = null)
        => new()
        {
            IsError = false,
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new BlobResourceContents
                    {
                        Uri = "audio://generated/speech",
                        MimeType = mimeType,
                        Blob = Encoding.UTF8.GetBytes(Convert.ToBase64String(audio))
                    }
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                status = "completed",
                mediaType = mimeType,
                byteLength = audio.Length,
                warnings,
                usage
            }, JsonSerializerOptions.Web)
        };

    private static CallToolResult CreateProgressCallToolResult(int chunkCount, long byteLength)
        => new()
        {
            IsError = false,
            Content = [new TextContentBlock { Text = "Speech generation is streaming." }],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                status = "streaming",
                chunkCount,
                byteLength
            }, JsonSerializerOptions.Web)
        };

    private static object CreateToolInput(SpeechRequest request)
        => new { text = request.Text, model = request.Model, voice = request.Voice };

    private static object CreateToolInput(AudioSpeechRequest request)
        => new { text = request.Input, model = request.Model, voice = request.Voice };

    private static Dictionary<string, JsonElement>? ToProviderOptions(Dictionary<string, object?>? metadata)
        => metadata?.Where(entry => entry.Value is not null).ToDictionary(
            entry => entry.Key,
            entry => entry.Value is JsonElement json
                ? json.Clone()
                : JsonSerializer.SerializeToElement(entry.Value, JsonSerializerOptions.Web));

    private static Dictionary<string, object?> CreateToolMetadata(
        string providerId,
        Dictionary<string, JsonElement>? metadata)
        => new() { [providerId] = metadata?.ToDictionary(entry => entry.Key, entry => (object)entry.Value.Clone()) ?? [] };

    private static string? ReadMetadataString(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;
        return value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : value.ToString();
    }

    private static float? ReadMetadataFloat(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is JsonElement json)
            return json.ValueKind == JsonValueKind.Number && json.TryGetSingle(out var number) ? number : null;
        return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ResolveMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "mulaw" => "audio/basic",
            "alaw" => "audio/basic",
            _ => "application/octet-stream"
        };

    private static AIStreamEvent CreateFinishEvent(string providerId, string? model, string id, string reason)
        => CreateEvent(providerId, "finish", id, new AIFinishEventData
        {
            FinishReason = reason,
            Model = model
        });

    private static AIStreamEvent CreateEvent(string providerId, string type, string id, object data)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data
            }
        };

    private static string CreateToolCallId(AIRequest request)
        => $"speech_{request.Id ?? Guid.NewGuid().ToString("N")}";

    private static string GetProviderModelId(string model, string providerId)
    {
        var prefix = providerId + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }
}
