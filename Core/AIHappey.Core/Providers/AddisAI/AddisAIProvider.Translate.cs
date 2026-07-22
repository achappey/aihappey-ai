using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AddisAI;

public partial class AddisAIProvider
{
    private async Task<AIResponse> ExecuteTranslationUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model ?? throw new ArgumentException("Model is required.", nameof(request));
        var (sourceLanguage, targetLanguage) = GetTranslationLanguages(model);
        var texts = ExtractMessages(request)
            .Where(message => !string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Content)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (texts.Length == 0)
            throw new ArgumentException("AddisAI translation requires text input.", nameof(request));

        var translations = new List<string>(texts.Length);
        Dictionary<string, object?>? lastMetadata = null;
        foreach (var text in texts)
        {
            var result = await TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
            translations.Add(result.Text);
            lastMetadata = result.Metadata;
        }

        return CreateTextResponse(
            model,
            string.Join("\n", translations),
            lastMetadata?.TryGetValue("usage", out var usage) == true && usage is Dictionary<string, object?> typedUsage ? typedUsage : [],
            lastMetadata ?? []);
    }

    private async Task<(string Text, Dictionary<string, object?> Metadata)> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (text.Length > 2000)
            throw new ArgumentException("AddisAI translation input cannot exceed 2000 characters.", nameof(text));

        ApplyAuthHeader();
        var payload = new { text, source_language = sourceLanguage, target_language = targetLanguage };
        var payloadJson = JsonSerializer.Serialize(payload, AddisJson);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/translate")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AddisAI translation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"AddisAI translation response did not include data: {raw}");

        var translated = GetRequiredString(data, "translation", raw);
        return (translated, new()
        {
            ["finishReason"] = "stop",
            ["sourceLanguage"] = sourceLanguage,
            ["targetLanguage"] = targetLanguage,
            ["quality"] = data.TryGetProperty("quality", out var quality) ? quality.GetString() : null,
            ["usage"] = BuildUsage(data),
            ["addisai.response.raw"] = root.Clone()
        });
    }
}
