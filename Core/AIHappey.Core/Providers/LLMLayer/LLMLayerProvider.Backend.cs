using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMLayer;

public partial class LLMLayerProvider
{
    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    private async Task<LLMLayerAnswerResponse> ExecuteAnswerAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonWeb);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/answer")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"LLMLayer answer request failed ({(int)resp.StatusCode}) {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        return new LLMLayerAnswerResponse
        {
            Answer = root.TryGetProperty("answer", out var answerEl)
                ? answerEl.Clone()
                : default,
            Sources = root.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array
                ? sourcesEl.Clone()
                : default,
            Images = root.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array
                ? imagesEl.Clone()
                : default,
            ResponseTime = root.TryGetProperty("response_time", out var rtEl) && rtEl.ValueKind == JsonValueKind.String
                ? rtEl.GetString()
                : null,
            InputTokens = TryGetInt32(root, "input_tokens"),
            OutputTokens = TryGetInt32(root, "output_tokens"),
            ModelCost = TryGetDecimal(root, "model_cost"),
            LlmlayerCost = TryGetDecimal(root, "llmlayer_cost")
        };
    }

    private async IAsyncEnumerable<LLMLayerStreamEvent> ExecuteAnswerStreamingAsync(
        Dictionary<string, object?> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonWeb);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v2/answer_stream")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"LLMLayer answer stream request failed ({(int)resp.StatusCode}) {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.Length == 0 || line.StartsWith(':'))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? (typeEl.GetString() ?? string.Empty)
                : string.Empty;

            yield return new LLMLayerStreamEvent
            {
                Type = type,
                Root = root.Clone()
            };

            if (string.Equals(type, "done", StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }

    private static Dictionary<string, object?> BuildAnswerPayload(
        string query,
        string model,
        float? temperature,
        int? maxTokens,
        JsonElement? llmlayerMetadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["model"] = model
        };

        if (temperature is not null)
            payload["temperature"] = temperature.Value;

        if (maxTokens is not null)
            payload["max_tokens"] = maxTokens.Value;

        MergeLlmlayerPayload(payload, llmlayerMetadata);
        return payload;
    }

    private static void MergeLlmlayerPayload(Dictionary<string, object?> payload, JsonElement? llmlayerMetadata)
    {
        if (llmlayerMetadata is null || llmlayerMetadata.Value.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in llmlayerMetadata.Value.EnumerateObject())
        {
            payload[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText(), JsonWeb);
        }
    }

    private JsonElement? ExtractLlmlayerMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(GetIdentifier(), out var raw) || raw is null)
            return null;

        if (raw is JsonElement je)
            return je.ValueKind == JsonValueKind.Object ? je : null;

        try
        {
            var el = JsonSerializer.SerializeToElement(raw, JsonWeb);
            return el.ValueKind == JsonValueKind.Object ? el : null;
        }
        catch
        {
            return null;
        }
    }

    private JsonElement? ExtractLlmlayerMetadata(ChatRequest chatRequest)
    {
        var metadata = chatRequest.GetProviderMetadata<JsonElement>(GetIdentifier());
        return metadata.ValueKind == JsonValueKind.Object ? metadata : null;
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

    private static object BuildUsage(LLMLayerAnswerResponse answer)
        => new
        {
            prompt_tokens = answer.InputTokens ?? 0,
            completion_tokens = answer.OutputTokens ?? 0,
            total_tokens = (answer.InputTokens ?? 0) + (answer.OutputTokens ?? 0),
            model_cost = answer.ModelCost,
            llmlayer_cost = answer.LlmlayerCost,
            response_time = answer.ResponseTime
        };

    private static string? TryExtractStructuredOutputSchemaString(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema?.Schema is JsonElement element
            && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return element.GetRawText();
        }

        return null;
    }

    private static void ApplyStructuredOutputIfAny(Dictionary<string, object?> payload, object? format)
    {
        var schema = TryExtractStructuredOutputSchemaString(format);
        if (string.IsNullOrWhiteSpace(schema))
            return;

        if (!payload.ContainsKey("answer_type"))
            payload["answer_type"] = "json";

        if (!payload.ContainsKey("json_schema"))
            payload["json_schema"] = schema;
    }

    private static string AnswerToText(JsonElement answer)
    {
        if (answer.ValueKind == JsonValueKind.String)
            return answer.GetString() ?? string.Empty;

        if (answer.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return answer.GetRawText();

        return string.Empty;
    }

    private static Dictionary<string, object?> BuildResponseMetadata(
        Dictionary<string, object?>? current,
        LLMLayerAnswerResponse response)
    {
        var merged = current is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(current);

        if (response.Sources.ValueKind == JsonValueKind.Array)
            merged["llmlayer_sources"] = JsonSerializer.Deserialize<object>(response.Sources.GetRawText(), JsonWeb);

        if (response.Images.ValueKind == JsonValueKind.Array)
            merged["llmlayer_images"] = JsonSerializer.Deserialize<object>(response.Images.GetRawText(), JsonWeb);

        if (!string.IsNullOrWhiteSpace(response.ResponseTime))
            merged["llmlayer_response_time"] = response.ResponseTime;

        return merged;
    }

    private static int? TryGetInt32(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val)
            ? val
            : null;

    private static decimal? TryGetDecimal(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;

        if (el.TryGetDecimal(out var d))
            return d;

        return null;
    }

    private sealed class LLMLayerAnswerResponse
    {
        public JsonElement Answer { get; init; }
        public JsonElement Sources { get; init; }
        public JsonElement Images { get; init; }
        public string? ResponseTime { get; init; }
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }
        public decimal? ModelCost { get; init; }
        public decimal? LlmlayerCost { get; init; }
    }

    private sealed class LLMLayerStreamEvent
    {
        public string Type { get; init; } = string.Empty;
        public JsonElement Root { get; init; }
    }
}

