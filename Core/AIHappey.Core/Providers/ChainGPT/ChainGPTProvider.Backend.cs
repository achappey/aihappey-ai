using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ChainGPT;

public partial class ChainGPTProvider
{
    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    private sealed class ChainGPTProviderMetadata
    {
        public string? ChatHistory { get; set; }
        public string? SdkUniqueId { get; set; }
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var msg in messages ?? [])
        {
            var text = ChatMessageContentExtensions.ToText(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{msg.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = string.Join("\n", message.Parts
                .OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        if (request.Input?.IsText == true)
            return request.Input.Text ?? string.Empty;

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            var lines = new List<string>();
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role.ToString().ToLowerInvariant();
                var text = message.Content.IsText
                    ? message.Content.Text
                    : string.Join("\n", message.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"{role}: {text}");
            }

            if (lines.Count > 0)
                return string.Join("\n\n", lines);
        }

        return request.Instructions ?? string.Empty;
    }

    private static Dictionary<string, object?> BuildChainGptPayload(
        string model,
        string question,
        ChainGPTProviderMetadata? metadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["question"] = question,
            ["chatHistory"] = string.IsNullOrWhiteSpace(metadata?.ChatHistory) ? "off" : metadata!.ChatHistory
        };

        if (!string.IsNullOrWhiteSpace(metadata?.SdkUniqueId))
            payload["sdkUniqueId"] = metadata!.SdkUniqueId;

        return payload;
    }

    private static ChainGPTProviderMetadata? TryExtractProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(nameof(ChainGPT).ToLowerInvariant(), out var raw) || raw is null)
            return null;

        try
        {
            var root = JsonSerializer.SerializeToElement(raw, JsonWeb);
            return root.ValueKind == JsonValueKind.Object
                ? root.Deserialize<ChainGPTProviderMetadata>(JsonWeb)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> CompleteQuestionBufferedAsync(
        string model,
        string question,
        ChainGPTProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(BuildChainGptPayload(model, question, metadata), JsonWeb),
                Encoding.UTF8,
                "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ChainGPT error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        return TryExtractBotText(raw, out var text) ? text! : raw;
    }

    private async IAsyncEnumerable<string> CompleteQuestionStreamingAsync(
        string model,
        string question,
        ChainGPTProviderMetadata? metadata,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(BuildChainGptPayload(model, question, metadata), JsonWeb),
                Encoding.UTF8,
                "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"ChainGPT stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var carry = new StringBuilder();
        var buffer = new char[2048];
        var done = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
                break;

            carry.Append(buffer, 0, read);

            var result = DrainCompleteLines(carry);

            foreach (var piece in result.Pieces)
                yield return piece;

            if (result.Done)
                yield break;
        }

        if (carry.Length > 0 && !done)
        {
            var result = ParseStreamLine(carry.ToString());

            foreach (var piece in result.Pieces)
                yield return piece;

            if (result.Done)
                yield break;
        }
    }

    private readonly record struct StreamParseResult(
        IEnumerable<string> Pieces,
        bool Done
    );


    private static StreamParseResult DrainCompleteLines(StringBuilder carry)
    {
        var snapshot = carry.ToString();
        var lastNewLine = snapshot.LastIndexOfAny(['\n', '\r']);
        if (lastNewLine < 0)
            return new([], false);

        var completed = snapshot[..(lastNewLine + 1)];
        var remainder = snapshot[(lastNewLine + 1)..];

        carry.Clear();
        carry.Append(remainder);

        var pieces = new List<string>();
        var done = false;

        var lines = completed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var result = ParseStreamLine(line);
            pieces.AddRange(result.Pieces);

            if (result.Done)
            {
                done = true;
                break;
            }
        }

        return new(pieces, done);
    }

    private static StreamParseResult ParseStreamLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new([], false);

        var payload = line.Trim();

        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            payload = payload["data:".Length..].Trim();

        if (payload is "[DONE]" or "[done]")
            return new([], true);

        if (TryExtractBotText(payload, out var bot) &&
            !string.IsNullOrWhiteSpace(bot))
            return new([bot!], false);

        if (TryExtractStreamJsonText(payload, out var jsonText) &&
            !string.IsNullOrWhiteSpace(jsonText))
            return new([jsonText!], false);

        return new([payload], false);
    }

    private static bool TryExtractBotText(string raw, out string? text)
    {
        text = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.String)
                    {
                        text = bot.GetString();
                        return true;
                    }
                }

                if (root.TryGetProperty("bot", out var rootBot) && rootBot.ValueKind == JsonValueKind.String)
                {
                    text = rootBot.GetString();
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractStreamJsonText(string raw, out string? text)
    {
        text = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (TryGetString(root, "delta", out text) ||
                TryGetString(root, "text", out text) ||
                TryGetString(root, "content", out text))
            {
                return true;
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (TryGetString(data, "delta", out text) ||
                    TryGetString(data, "text", out text) ||
                    TryGetString(data, "content", out text) ||
                    TryGetString(data, "bot", out text))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement root, string property, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
            return false;

        value = el.GetString();
        return true;
    }
}
