using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AddisAI;

public partial class AddisAIProvider
{
    private const string ChatModel = "addis-1-alef";
    private const string TranslationPrefix = "translate/";
    private const string TtsPrefix = "tts/";
    private const string SttPrefix = "stt/";

    private static readonly string[] SupportedLanguages = ["am", "om", "en"];
    private static readonly string[] SpeechLanguages = ["am", "om"];

    private static readonly JsonSerializerOptions AddisJson = new(JsonSerializerDefaults.Web);

    private string NormalizeModelId(string? model)
    {
        var normalized = string.IsNullOrWhiteSpace(model) ? ChatModel : model.Trim();
        var prefix = GetIdentifier() + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..].Trim('/')
            : normalized.Trim('/');
    }

    private static bool IsSupportedLanguage(string language)
        => SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);

    private static bool IsSupportedSpeechLanguage(string language)
        => SpeechLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);

    private string GetTtsLanguage(string model)
    {
        var local = NormalizeModelId(model);
        if (!local.StartsWith(TtsPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"AddisAI TTS model must be '{GetIdentifier()}/tts/{{am|om}}'.", nameof(model));

        var language = local[TtsPrefix.Length..].Trim();
        if (!IsSupportedSpeechLanguage(language))
            throw new ArgumentException($"Unsupported AddisAI TTS language '{language}'.", nameof(model));

        return language.ToLowerInvariant();
    }

    private string GetSttLanguage(string model)
    {
        var local = NormalizeModelId(model);
        if (!local.StartsWith(SttPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"AddisAI STT model must be '{GetIdentifier()}/stt/{{am|om}}'.", nameof(model));

        var language = local[SttPrefix.Length..].Trim();
        if (!IsSupportedSpeechLanguage(language))
            throw new ArgumentException($"Unsupported AddisAI STT language '{language}'.", nameof(model));

        return language.ToLowerInvariant();
    }

    private string GetChatTargetLanguage(string model)
    {
        var local = NormalizeModelId(model);
        if (!string.Equals(local, ChatModel, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"AddisAI chat model must be '{GetIdentifier()}/{ChatModel}'.", nameof(model));

        return "am";
    }

    private (string Source, string Target) GetTranslationLanguages(string model)
    {
        var local = NormalizeModelId(model);
        var parts = local.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "translate", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"AddisAI translation model must be '{GetIdentifier()}/translate/{{source}}/{{target}}'.", nameof(model));

        var source = parts[1].ToLowerInvariant();
        var target = parts[2].ToLowerInvariant();
        if (!IsSupportedLanguage(source) || !IsSupportedLanguage(target) || string.Equals(source, target, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported AddisAI translation pair '{parts[1]}/{parts[2]}'.", nameof(model));

        return (source, target);
    }

    private static List<(string Role, string Content)> ExtractMessages(AIRequest request)
    {
        var messages = new List<(string Role, string Content)>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(("system", request.Instructions));
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(("user", request.Input.Text));

        foreach (var item in request.Input?.Items ?? [])
        {
            var text = string.Join("\n", item.Content?.OfType<AITextContentPart>()
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                messages.Add((string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role!, text));
        }

        return messages;
    }

    private static Dictionary<string, object?> BuildUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage_metadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return new()
            {
                ["inputTokens"] = TryReadInt(usage, "prompt_token_count"),
                ["outputTokens"] = TryReadInt(usage, "candidates_token_count"),
                ["totalTokens"] = TryReadInt(usage, "total_token_count")
            };
        }

        return [];
    }

    private static int? TryReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string GetRequiredString(JsonElement element, string name, string rawResponse)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            return value.GetString()!;

        throw new InvalidOperationException($"AddisAI response did not include '{name}': {rawResponse}");
    }

    private static string GetAudioExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/webm" => ".webm",
            _ => ".audio"
        };
}
