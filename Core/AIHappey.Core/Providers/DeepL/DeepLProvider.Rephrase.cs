using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    private const string RephraseModelId = "rephrase";

    private static bool IsRephraseModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var trimmed = modelId.Trim();
        return string.Equals(trimmed, RephraseModelId, StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith($"/{RephraseModelId}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<string>> ProcessTextsAsync(
        List<string> texts,
        string modelId,
        CancellationToken cancellationToken)
        => IsRephraseModel(modelId)
            ? await RephraseAsync(texts, cancellationToken)
            : await TranslateAsync(texts, modelId, cancellationToken);

    private async Task<IReadOnlyList<string>> RephraseAsync(
        List<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));

        var payload = new Dictionary<string, object?>
        {
            ["text"] = texts
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/write/rephrase")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL rephrase failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<DeepLRephraseResponse>(body, JsonSerializerOptions.Web);
        var improvements = parsed?.Improvements ?? [];

        if (improvements.Count == 0)
            return [.. texts.Select(_ => string.Empty)];

        var result = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var text = (i < improvements.Count)
                ? improvements[i].Text
                : null;

            result.Add(text ?? string.Empty);
        }

        return result;
    }

    private sealed class DeepLRephraseResponse
    {
        public List<DeepLRephraseImprovement>? Improvements { get; set; }
    }

    private sealed class DeepLRephraseImprovement
    {
        public string? Text { get; set; }
    }
}
