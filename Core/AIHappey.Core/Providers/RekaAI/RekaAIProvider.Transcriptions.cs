using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    private const string RekaTranscriptionInstruction =
        "Transcribe the supplied audio faithfully. Return only the transcript, with no preamble, labels, commentary, or formatting.";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("Media type is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var warnings = BuildRekaTranscriptionWarnings(metadata);
        var model = NormalizeRekaTranscriptionModelId(request.Model);
        var audioBase64 = NormalizeRekaTranscriptionAudioData(request.Audio);
        var payload = BuildRekaTranscriptionPayload(model, audioBase64, request.MediaType, metadata, stream: false);
        var requestBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var timestamp = DateTime.UtcNow;

        using var content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.PostAsync("v1/chat/completions", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RekaAI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ExtractRekaTranscriptionText(root);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("RekaAI did not return transcription text.");

        return new TranscriptionResponse
        {
            Text = text,
            Segments = [],
            Warnings = warnings,
            ProviderMetadata = BuildRekaTranscriptionProviderMetadata(root),
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = root
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }

    private static Dictionary<string, object?> BuildRekaTranscriptionPayload(
        string model,
        string audioBase64,
        string mediaType,
        JsonElement metadata,
        bool stream)
    {
        ValidateRekaTranscriptionMetadata(metadata);
        var prompt = ReadOptionalReference<string>(metadata, "prompt");
        var temperature = ReadOptionalValue<float>(metadata, "temperature");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "audio_url",
                            audio_url = $"data:{mediaType};base64,{audioBase64}"
                        },
                        new
                        {
                            type = "text",
                            text = BuildRekaTranscriptionInstruction(prompt)
                        }
                    }
                }
            }
        };

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (IsRekaTranscriptionMappedOption(property.Name))
                    continue;

                payload.TryAdd(property.Name, property.Value.Clone());
            }
        }

        if (temperature is not null)
            payload["temperature"] = temperature.Value;

        if (stream)
            payload["stream"] = true;

        return payload;
    }

    private static string BuildRekaTranscriptionInstruction(string? prompt)
        => string.IsNullOrWhiteSpace(prompt)
            ? RekaTranscriptionInstruction
            : $"{RekaTranscriptionInstruction}\n\nAdditional transcription instructions: {prompt.Trim()}";

    private static string NormalizeRekaTranscriptionModelId(string model)
    {
        var normalized = model.Trim();
        var providerPrefix = nameof(RekaAI).ToLowerInvariant() + "/";

        return normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[providerPrefix.Length..]
            : normalized;
    }

    private static string NormalizeRekaTranscriptionAudioData(object audio)
    {
        var audioData = audio switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? string.Empty,
            JsonElement json => json.ToString(),
            _ => audio.ToString() ?? string.Empty
        };

        audioData = audioData.Trim();
        if (audioData.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || audioData.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("RekaAI transcription requires base64 audio data; remote audio URLs are not supported.", nameof(audio));
        }

        if (audioData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = audioData.IndexOf(',');
            var metadata = separatorIndex > 0 ? audioData[..separatorIndex] : string.Empty;

            if (separatorIndex < 0 || !metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Audio data URLs must use base64 encoding.", nameof(audio));

            audioData = audioData[(separatorIndex + 1)..];
        }

        if (string.IsNullOrWhiteSpace(audioData))
            throw new ArgumentException("Audio data is required.", nameof(audio));

        try
        {
            return Convert.ToBase64String(Convert.FromBase64String(audioData));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be valid base64 data.", nameof(audio), exception);
        }
    }

    private static List<object> BuildRekaTranscriptionWarnings(JsonElement metadata)
    {
        var warnings = new List<object>();
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return warnings;

        ValidateRekaTranscriptionMetadata(metadata);

        if (!string.IsNullOrWhiteSpace(ReadOptionalReference<string>(metadata, "language")))
            AddUnsupportedWarning(warnings, "language", "Reka's chat API does not expose a transcription language parameter.");

        if (ReadOptionalReference<string[]>(metadata, "timestamp_granularities") is { Length: > 0 })
            AddUnsupportedWarning(warnings, "timestamp_granularities", "Reka's chat API does not return timestamped transcription segments.");

        if (ReadOptionalValue<int>(metadata, "sampling_rate") is not null)
            AddUnsupportedWarning(warnings, "sampling_rate", "The retired Reka transcription endpoint's sampling-rate option is not supported by chat completions.");

        if (!string.IsNullOrWhiteSpace(ReadOptionalReference<string>(metadata, "target_language")))
            AddUnsupportedWarning(warnings, "target_language", "Translation is not supported by the chat-backed transcription flow.");

        if (ReadOptionalValue<bool>(metadata, "is_translate") is not null)
            AddUnsupportedWarning(warnings, "is_translate", "Translation is not supported by the chat-backed transcription flow.");

        if (ReadOptionalValue<bool>(metadata, "return_translation_audio") is not null)
            AddUnsupportedWarning(warnings, "return_translation_audio", "Translated audio output is not supported by the chat-backed transcription flow.");

        if (ReadOptionalValue<int>(metadata, "max_tokens") is not null)
            AddUnsupportedWarning(warnings, "max_tokens", "The retired Reka transcription endpoint's max-tokens option is not supported by chat completions.");

        return warnings;
    }

    private static void ValidateRekaTranscriptionMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("RekaAI transcription provider metadata must be a JSON object.");

        ReadOptionalReference<string>(metadata, "prompt");
        ReadOptionalValue<float>(metadata, "temperature");
        ReadOptionalReference<string>(metadata, "language");
        ReadOptionalReference<string[]>(metadata, "timestamp_granularities");
        ReadOptionalValue<int>(metadata, "sampling_rate");
        ReadOptionalReference<string>(metadata, "target_language");
        ReadOptionalValue<bool>(metadata, "is_translate");
        ReadOptionalValue<bool>(metadata, "return_translation_audio");
        ReadOptionalValue<int>(metadata, "max_tokens");
    }

    private static bool IsRekaTranscriptionMappedOption(string name)
        => name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("temperature", StringComparison.OrdinalIgnoreCase)
            || name.Equals("language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp_granularities", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sampling_rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("target_language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("is_translate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("return_translation_audio", StringComparison.OrdinalIgnoreCase)
            || name.Equals("max_tokens", StringComparison.OrdinalIgnoreCase);

    private static T? ReadOptionalReference<T>(JsonElement metadata, string name)
        where T : class
    {
        var value = GetOptionalMetadataValue(metadata, name);
        return value?.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static T? ReadOptionalValue<T>(JsonElement metadata, string name)
        where T : struct
    {
        var value = GetOptionalMetadataValue(metadata, name);
        return value?.Deserialize<T?>(JsonSerializerOptions.Web);
    }

    private static JsonElement? GetOptionalMetadataValue(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        JsonElement? result = null;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                result = property.Value;
        }

        return result?.ValueKind == JsonValueKind.Null ? null : result;
    }

    private static void AddUnsupportedWarning(ICollection<object> warnings, string feature, string reason)
        => warnings.Add(new
        {
            type = "unsupported",
            feature,
            reason
        });

    private Dictionary<string, JsonElement>? BuildRekaTranscriptionProviderMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var rekaMetadata = new Dictionary<string, JsonElement>
        {
            ["usage"] = usage.Clone()
        };

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(rekaMetadata, JsonSerializerOptions.Web)
        };
    }

    private static string ExtractRekaTranscriptionText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && TryExtractRekaTextContent(content, out var text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool TryExtractRekaTextContent(JsonElement content, out string text)
    {
        text = content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(text))
            return true;

        if (content.ValueKind != JsonValueKind.Array)
            return false;

        var textParts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                && item.TryGetProperty("text", out var textProperty)
                && textProperty.ValueKind == JsonValueKind.String)
            {
                textParts.Add(textProperty.GetString() ?? string.Empty);
            }
        }

        text = string.Concat(textParts);
        return !string.IsNullOrWhiteSpace(text);
    }
}
