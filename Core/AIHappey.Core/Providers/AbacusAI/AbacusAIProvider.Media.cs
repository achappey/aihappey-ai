using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AbacusAI;

public sealed partial class AbacusAIProvider
{
    private static JsonObject MediaPayload(string model, object messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return new JsonObject
        {
            ["model"] = model.StartsWith("abacusai/", StringComparison.Ordinal) ? model["abacusai/".Length..] : model,
            ["messages"] = JsonSerializer.SerializeToNode(messages)
        };
    }

    private static JsonObject MediaOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue("abacusai", out var value) != true)
            return new();
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new();
        return JsonNode.Parse(value.GetRawText()) as JsonObject
            ?? throw new ArgumentException("providerOptions.abacusai must be a JSON object.");
    }

    private static void MergeMediaOptions(JsonObject target, JsonObject options)
    {
        foreach (var property in options)
        {
            if (target[property.Key] is JsonObject nested && property.Value is JsonObject overrides)
                MergeMediaOptions(nested, overrides);
            else
                target[property.Key] = property.Value?.DeepClone();
        }
    }

    private async Task<(JsonElement Root, IDictionary<string, string> Headers)> SendMediaAsync(
        JsonObject payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        payload["stream"] = false;
        using var response = await _client.PostAsJsonAsync("v1/chat/completions", payload, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AbacusAI media error ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        ThrowMediaError(root);
        return (root, response.GetHeaders());
    }

    private static void ThrowMediaError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"AbacusAI media error: {error.GetRawText()}");
    }

    private static JsonElement MediaMessage(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message))
        {
            if (choices[0].TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null
                && finish.GetString() != "stop")
                throw new InvalidOperationException($"AbacusAI media terminated with finish_reason '{finish}'.");
            if (ReadString(message, "refusal") is { Length: > 0 } refusal)
                throw new InvalidOperationException($"AbacusAI refused media request: {refusal}");
            return message;
        }
        throw new InvalidOperationException("AbacusAI media response did not contain an assistant message.");
    }

    private Dictionary<string, JsonElement> MediaMetadata(JsonElement root, bool? emulated = null)
    {
        var usage = root.TryGetProperty("usage", out var value) ? value.Clone() : (JsonElement?)null;
        decimal? cost = usage is { ValueKind: JsonValueKind.Object } u
            && u.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
            && c.TryGetDecimal(out var amount) ? amount : null;
        return GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            id = ReadString(root, "id"), usage, emulated
        }, cost);
    }

    private ResponseData MediaResponse(JsonElement root, string model, IDictionary<string, string> headers)
        => new()
        {
            Timestamp = ReadInt64(root, "created") is { } created
                ? DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime : DateTime.UtcNow,
            ModelId = (ReadString(root, "model") ?? model).ToModelId(GetIdentifier()),
            Headers = headers,
            Body = root
        };

    private static string AudioFormatName(string? format, bool input = false)
    {
        var name = (format ?? "mp3").Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "mpeg" or "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" or "wave" => "wav",
            "audio/opus" => "opus",
            "audio/ogg" => "ogg",
            "audio/aac" => "aac",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/webm" => "webm",
            "audio/mp4" or "audio/x-m4a" or "mp4" => "m4a",
            "audio/pcm" => "pcm",
            var other => other
        };
        var supported = input
            ? new[] { "wav", "mp3", "ogg", "flac", "webm", "m4a", "aac", "pcm", "mpga" }
            : ["mp3", "wav", "opus", "aac", "flac"];
        if (!supported.Contains(name))
            throw new NotSupportedException($"AbacusAI does not document the {(input ? "input" : "output")} audio format '{format}'.");
        return name;
    }

    private static string AudioMime(string format) => format switch
    {
        "mp3" => "audio/mpeg", "opus" => "audio/opus", _ => $"audio/{format}"
    };

    private static string AudioBase64(object audio)
    {
        var value = audio is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (MediaContentHelpers.TryParseDataUrl(value, out _, out var data))
            value = data;
        if (Convert.FromBase64String(value).Length == 0)
            throw new ArgumentException("Audio must not be empty.");
        return value;
    }
}
