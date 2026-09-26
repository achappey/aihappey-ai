using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.GMICloud;

public partial class GMICloudProvider
{
    private const string AutorouteUrl = "https://console.gmicloud.ai/api/v1/ie/recommendation/autoroute";

    private static bool TryGetAutorouteMode(string? model, out string mode)
    {
        var local = model?.StartsWith("gmicloud/", StringComparison.OrdinalIgnoreCase) == true
            ? model["gmicloud/".Length..]
            : model;

        mode = local?.ToLowerInvariant() switch
        {
            "autoroute-cost" => "cost",
            "autoroute-balanced" => "balanced",
            "autoroute-quality" => "quality",
            _ => ""
        };

        return mode.Length > 0;
    }

    private HttpRequestMessage CreateAutorouteRequest(ChatCompletionOptions options, string mode, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AutorouteUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            messages = options.Messages,
            mode,
            stream
        }), Encoding.UTF8, "application/json");
        return request;
    }

    private static string PrefixSelectedModel(string? model, string fallback)
    {
        if (string.IsNullOrWhiteSpace(model))
            return fallback.StartsWith("gmicloud/", StringComparison.OrdinalIgnoreCase) ? fallback : $"gmicloud/{fallback}";

        return model.StartsWith("gmicloud/", StringComparison.OrdinalIgnoreCase) ? model : $"gmicloud/{model}";
    }

    private async Task<ChatCompletion> CompleteAutorouteAsync(
        ChatCompletionOptions options, string mode, CancellationToken cancellationToken)
    {
        using var request = CreateAutorouteRequest(options, mode, false);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAutorouteSuccessAsync(response, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("GMICloud autoroute returned an invalid completion.");

        // The router's one-shot response has a top-level message rather than OpenAI choices.
        // It may also return OpenAI-compatible completions; accept both shapes.
        var isCompletion = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array;
        if (!isCompletion && (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object))
            throw new InvalidOperationException("GMICloud autoroute returned no assistant message.");

        var fields = root.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        fields["model"] = JsonSerializer.SerializeToElement(PrefixSelectedModel(
            ReadString(root, "model") ?? ReadSelectedModel(root), options.Model));
        if (!isCompletion)
        {
            fields.Remove("message");
            fields["choices"] = JsonSerializer.SerializeToElement(new[] { new
            {
                index = 0,
                message = root.GetProperty("message").Clone(),
                finish_reason = "stop"
            } });
        }

        var result = JsonSerializer.Deserialize<ChatCompletion>(JsonSerializer.Serialize(fields), JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("GMICloud autoroute returned an invalid completion.");
        if (result.Created == 0)
            result.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return result;
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamAutorouteAsync(
        ChatCompletionOptions options, string mode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateAutorouteRequest(options, mode, true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAutorouteSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? eventName = null;
        ChatCompletionUpdate? finalChunk = null;
        JsonElement? routingMetadata = null;
        string? selectedModel = null;
        var sawCompletion = false;

        // Hold the terminal chunk until the router's trailing routing_metadata event arrives.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length != 0)
            {
                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    eventName = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line[5..].TrimStart());
                }
                continue;
            }

            if (data.Length == 0)
            {
                eventName = null;
                continue;
            }

            var text = data.ToString();
            data.Clear();
            var name = eventName;
            eventName = null;
            if (text == "[DONE]") break;

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (name?.Equals("error", StringComparison.OrdinalIgnoreCase) == true ||
                (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _)))
                throw new HttpRequestException($"GMICloud autoroute stream error: {text}");

            if (name?.Equals("routing_metadata", StringComparison.OrdinalIgnoreCase) == true ||
                (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("routing_metadata", out _)
                    && !root.TryGetProperty("choices", out _)))
            {
                routingMetadata = root.TryGetProperty("routing_metadata", out var nested) ? nested.Clone() : root.Clone();
                selectedModel = ReadString(routingMetadata.Value, "selected_model") ?? selectedModel;
                continue;
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var chunkChoices)
                || chunkChoices.ValueKind != JsonValueKind.Array)
                continue;

            sawCompletion = true;
            var chunk = JsonSerializer.Deserialize<ChatCompletionUpdate>(text, JsonSerializerOptions.Web)!;
            chunk.Model = PrefixSelectedModel(chunk.Model, options.Model);
            if (chunk.Created == 0) chunk.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (finalChunk is not null)
            {
                yield return finalChunk;
                finalChunk = null;
            }

            if (chunkChoices.EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var reason)
                && reason.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)))
                finalChunk = chunk;
            else
                yield return chunk;
        }

        if (!sawCompletion)
            throw new InvalidOperationException("GMICloud autoroute stream returned no completion chunks.");

        if (finalChunk is not null)
        {
            if (selectedModel is not null)
                finalChunk.Model = PrefixSelectedModel(selectedModel, options.Model);
            if (routingMetadata is not null)
            {
                finalChunk.AdditionalProperties ??= [];
                finalChunk.AdditionalProperties["routing_metadata"] = routingMetadata.Value;
            }
            yield return finalChunk;
        }
    }

    private static string? ReadSelectedModel(JsonElement root)
        => root.TryGetProperty("routing_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? ReadString(metadata, "selected_model") : null;

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task EnsureAutorouteSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"GMICloud autoroute HTTP {(int)response.StatusCode}: {body}",
            null, response.StatusCode);
    }
}
