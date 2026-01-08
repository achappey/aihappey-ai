using System.Text.Json;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static (List<string> ImageUrls, List<string> Texts) ExtractImagesAndTextFromSyncResponse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("output", out var output))
            return ([], []);
        if (!output.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return ([], []);

        List<string> urls = [];
        List<string> texts = [];

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var msg))
                continue;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase)
                    || (type is null && part.TryGetProperty("image", out _)))
                {
                    if (part.TryGetProperty("image", out var imgEl))
                    {
                        var url = imgEl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            urls.Add(url);
                    }
                }
                else if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (part.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            texts.Add(text);
                    }
                }
            }
        }

        return (urls, texts);
    }
}

