using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Baseten;

public sealed partial class BasetenProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        NormalizeAudioInputs(options);
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        NormalizeAudioInputs(options);
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options, cancellationToken: cancellationToken);
    }

    private static void NormalizeAudioInputs(ChatCompletionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Messages = options.Messages.Select(NormalizeAudioInputs).ToList();
    }

    private static ChatMessage NormalizeAudioInputs(ChatMessage message)
    {
        if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
            || message.Content.ValueKind != JsonValueKind.Array)
            return message;

        var changed = false;
        var normalizedParts = new List<JsonElement>();

        foreach (var part in message.Content.EnumerateArray())
        {
            if (!TryNormalizeAudioInput(part, out var normalizedPart))
            {
                normalizedParts.Add(part.Clone());
                continue;
            }

            changed = true;
            normalizedParts.Add(normalizedPart);
        }

        if (!changed)
            return message;

        return new ChatMessage
        {
            Role = message.Role,
            ToolCallId = message.ToolCallId,
            ToolCalls = message.ToolCalls,
            Content = JsonSerializer.SerializeToElement(normalizedParts)
        };
    }

    private static bool TryNormalizeAudioInput(JsonElement part, out JsonElement normalizedPart)
    {
        normalizedPart = default;

        if (part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "input_audio", StringComparison.OrdinalIgnoreCase)
            || !part.TryGetProperty("input_audio", out var inputAudio)
            || inputAudio.ValueKind != JsonValueKind.Object
            || !inputAudio.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(data.GetString()))
            return false;

        var value = data.GetString()!;
        var format = inputAudio.TryGetProperty("format", out var formatElement)
                     && formatElement.ValueKind == JsonValueKind.String
            ? formatElement.GetString()
            : null;
        var url = IsBasetenAudioUrl(value)
            ? value
            : $"data:{ToAudioMediaType(format)};base64,{value}";

        normalizedPart = JsonSerializer.SerializeToElement(new
        {
            type = "audio_url",
            audio_url = new { url }
        });

        return true;
    }

    private static bool IsBasetenAudioUrl(string value)
        => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
           || Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string ToAudioMediaType(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return "audio/wav";

        var normalized = format.Trim().ToLowerInvariant();
        if (normalized.StartsWith("audio/", StringComparison.Ordinal))
            return normalized;

        return normalized switch
        {
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            "wave" => "audio/wav",
            _ => $"audio/{normalized}"
        };
    }
}

