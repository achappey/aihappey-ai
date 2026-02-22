using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Synexa;
using AIHappey.Core.AI;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    private static readonly JsonSerializerOptions SynexaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class SynexaPredictionResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = default!;

        [JsonPropertyName("model")]
        public string Model { get; set; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; set; } = default!;

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public string? CompletedAt { get; set; }

        [JsonPropertyName("output")]
        public JsonElement Output { get; set; }

        [JsonPropertyName("metrics")]
        public JsonElement Metrics { get; set; }
    }

    private static bool IsPredictionTerminal(string? status)
        => string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    private static string ToSynexaModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        const string prefix = "synexa/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private static (string waitType, int intervalMs, int timeoutSeconds) ResolveWaitOptions(SynexaWaitOptions? wait)
    {
        var waitType = string.Equals(wait?.Type, "poll", StringComparison.OrdinalIgnoreCase)
            ? "poll"
            : "block";

        var intervalMs = Math.Clamp(wait?.IntervalMs ?? 500, 100, 5000);
        var timeoutSeconds = Math.Clamp(wait?.TimeoutSeconds ?? 60, 10, 600);
        return (waitType, intervalMs, timeoutSeconds);
    }

    private async Task<SynexaPredictionResponse> CreatePredictionAsync(
        string model,
        object input,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = new
        {
            model = ToSynexaModel(model),
            input
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "predictions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SynexaJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Synexa prediction create failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<SynexaPredictionResponse>(raw, SynexaJson)
               ?? throw new InvalidOperationException("Synexa prediction create returned empty body.");
    }

    private async Task<SynexaPredictionResponse> GetPredictionAsync(string id, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, $"predictions/{id}");
        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Synexa prediction get failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<SynexaPredictionResponse>(raw, SynexaJson)
               ?? throw new InvalidOperationException("Synexa prediction get returned empty body.");
    }

    private async Task<SynexaPredictionResponse> WaitPredictionAsync(
        SynexaPredictionResponse prediction,
        SynexaWaitOptions? wait,
        CancellationToken cancellationToken)
    {
        var (waitType, intervalMs, timeoutSeconds) = ResolveWaitOptions(wait);

        if (waitType == "block")
        {
            try
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Post, $"predictions/{prediction.Id}/wait")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { timeout = timeoutSeconds }, SynexaJson),
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json)
                };

                using var resp = await _client.SendAsync(req, cancellationToken);
                var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

                if (resp.IsSuccessStatusCode)
                {
                    var blocked = JsonSerializer.Deserialize<SynexaPredictionResponse>(raw, SynexaJson)
                                  ?? throw new InvalidOperationException("Synexa prediction wait returned empty body.");

                    if (!IsPredictionTerminal(blocked.Status))
                        return await PollPredictionUntilTerminalAsync(blocked, intervalMs, timeoutSeconds, cancellationToken);

                    EnsurePredictionSucceeded(blocked);
                    return blocked;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // fallback to polling
            }
        }

        var polled = await PollPredictionUntilTerminalAsync(prediction, intervalMs, timeoutSeconds, cancellationToken);
        EnsurePredictionSucceeded(polled);
        return polled;
    }

    private async Task<SynexaPredictionResponse> PollPredictionUntilTerminalAsync(
        SynexaPredictionResponse prediction,
        int intervalMs,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (IsPredictionTerminal(prediction.Status))
            return prediction;

        return await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: _ => GetPredictionAsync(prediction.Id, cancellationToken),
            isTerminal: p => IsPredictionTerminal(p.Status),
            interval: TimeSpan.FromMilliseconds(intervalMs),
            timeout: TimeSpan.FromSeconds(timeoutSeconds),
            maxAttempts: null,
            cancellationToken: cancellationToken);
    }

    private static void EnsurePredictionSucceeded(SynexaPredictionResponse prediction)
    {
        if (string.Equals(prediction.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(prediction.Error)
                ? $"Synexa prediction failed with status '{prediction.Status}'."
                : $"Synexa prediction failed: {prediction.Error}");
    }

    private static IEnumerable<string> ExtractStringOutputs(JsonElement output)
    {
        if (output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        List<string> values = [];

        void Visit(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var s = element.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        values.Add(s);
                    break;

                case JsonValueKind.Object:
                    if (element.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                    {
                        var url = urlEl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            values.Add(url!);
                    }

                    foreach (var property in element.EnumerateObject())
                        Visit(property.Value);
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Visit(item);
                    break;
            }
        }

        Visit(output);
        return values;
    }

    private async Task<string> DownloadImageAsDataUrlAsync(string value, CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme is not "http" and not "https"))
        {
            return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? value
                : Common.Extensions.ImageExtensions.ToDataUrl(value, MediaTypeNames.Image.Png);
        }

        using var resp = await _client.GetAsync(value, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Failed to download Synexa image output ({(int)resp.StatusCode}).");

        var mimeType = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            mimeType = MediaTypeNames.Image.Png;

        return Common.Extensions.ImageExtensions.ToDataUrl(Convert.ToBase64String(bytes), mimeType);
    }

    private static string ExtractOutputText(JsonElement output)
    {
        if (output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        if (output.ValueKind == JsonValueKind.String)
            return output.GetString() ?? string.Empty;

        if (output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? string.Empty;

            return output.GetRawText();
        }

        if (output.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    parts.Add(t.GetString() ?? string.Empty);
                    continue;
                }

                parts.Add(item.GetRawText());
            }

            return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        return output.GetRawText();
    }

    private static DateTimeOffset ParseTimestampOrNow(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UtcNow;

    private static string BuildPromptFromChatCompletionOptions(ChatCompletionOptions options)
    {
        var lines = new List<string>();

        foreach (var message in options.Messages ?? [])
        {
            var role = message.Role ?? "user";
            var text = ChatMessageContentExtensions.ToText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                lines.Add($"System: {text}");
            else
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var lines = new List<string>();

        foreach (var message in messages)
        {
            var role = message.Role.ToString();
            var textParts = message.Parts.OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t));

            var text = string.Join("\n", textParts);
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromResponseInput(ResponseInput? input)
    {
        if (input is null)
            return string.Empty;

        if (input.IsText)
            return input.Text ?? string.Empty;

        if (input.IsItems != true || input.Items is null)
            return string.Empty;

        var lines = new List<string>();
        foreach (var item in input.Items)
        {
            if (item is not ResponseInputMessage msg)
                continue;

            var role = msg.Role.ToString().ToLowerInvariant();
            var text = msg.Content.IsText
                ? msg.Content.Text
                : string.Join("\n", msg.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{role}: {text}");
        }

        return string.Join("\n\n", lines);
    }
}

