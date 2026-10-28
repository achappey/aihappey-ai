using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Isaacus;

public partial class IsaacusProvider
{
    private async Task<NativeResult> SendAsync(
        string endpoint, Dictionary<string, JsonElement> payload, CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Isaacus API key.");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Isaacus {endpoint} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected a response object.");
            return new NativeResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw InvalidResponse(endpoint, exception);
        }
    }

    private static string NativeModel(string model)
    {
        var native = model.StartsWith("isaacus/", StringComparison.OrdinalIgnoreCase)
            ? model["isaacus/".Length..] : model;
        if (string.IsNullOrWhiteSpace(native))
            throw new ArgumentException("Model is required.", nameof(model));
        return native;
    }

    private static int InputTokens(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("input_tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Number
            || !tokens.TryGetInt32(out var value) || value < 0)
            throw new JsonException("Expected usage.input_tokens to be a non-negative integer.");
        return value;
    }

    private static int ReadIndex(JsonElement item, int count, HashSet<int> seen)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index)
            || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var value)
            || value < 0 || value >= count || !seen.Add(value))
            throw new JsonException("Expected a unique input index.");
        return value;
    }

    private static float ReadFloat(JsonElement item)
        => item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var value) && float.IsFinite(value)
            ? value : throw new JsonException("Expected a finite float.");

    private static InvalidOperationException InvalidResponse(string endpoint, JsonException exception)
        => new($"Isaacus {endpoint} request returned an invalid response.", exception);

    private sealed record NativeResult(JsonElement Root, IDictionary<string, string> Headers);
}
