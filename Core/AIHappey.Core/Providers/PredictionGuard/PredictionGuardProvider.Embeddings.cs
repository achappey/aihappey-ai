using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PredictionGuard;

public sealed partial class PredictionGuardProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var (result, root) = await SendEmbeddingAsync(request, cancellationToken);
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            result.Response.Usage = null!;
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var (result, root) = await SendEmbeddingAsync(
            request.ToOpenAIEmbeddingRequest(GetIdentifier()), cancellationToken);
        var response = result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(root));
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            response.Usage = null;
        response.Response!.Body = root;
        return response;
    }

    private async Task<(OpenAICompatibleEmbeddingResult Result, JsonElement Root)> SendEmbeddingAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        if (request.Dimensions is not null || request.User is not null ||
            request.EncodingFormat is not null && request.EncodingFormat != "float")
            throw new NotSupportedException("Prediction Guard documents numeric embeddings without dimensions or user controls.");

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in request.AdditionalProperties ?? [])
            payload[property.Key] = property.Value.Clone();
        payload["model"] = JsonSerializer.SerializeToElement(NativeModel(request.Model));
        payload["input"] = request.Input.Clone();

        var (root, headers) = await SendJsonAsync("embeddings", payload, cancellationToken);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected Prediction Guard embedding data.");
        var count = request.Input.ValueKind == JsonValueKind.String ||
            request.Input[0].ValueKind == JsonValueKind.Number ? 1 : request.Input.GetArrayLength();
        if (data.GetArrayLength() != count)
            throw new JsonException("Expected one embedding per input.");
        var seen = new HashSet<int>();
        foreach (var item in data.EnumerateArray())
        {
            _ = ReadIndex(item, count, seen);
            if (!item.TryGetProperty("embedding", out var vector) ||
                vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() == 0)
                throw new JsonException("Expected a non-empty numeric embedding.");
            foreach (var value in vector.EnumerateArray())
                _ = ReadFloat(value);
        }
        var result = root.Deserialize<OpenAIEmbeddingResponse>(JsonSerializerOptions.Web)
            ?? throw new JsonException("Expected a Prediction Guard embedding response.");
        result.Usage ??= new();
        result.Model = $"{GetIdentifier()}/{NativeModel(result.Model ?? request.Model)}";
        return (new OpenAICompatibleEmbeddingResult(result, headers), root);
    }

    private async Task<(JsonElement Root, IDictionary<string, string> Headers)> SendJsonAsync(
        string endpoint, Dictionary<string, JsonElement> payload, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(payload) };
        message.Headers.Authorization = new("Bearer", ResolveKey());
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return (document.RootElement.Clone(), response.GetHeaders());
    }

    private static int ReadIndex(JsonElement item, int count, HashSet<int> seen)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
            !index.TryGetInt32(out var value) || value < 0 || value >= count || !seen.Add(value))
            throw new JsonException("Expected a unique valid input index.");
        return value;
    }

    private static float ReadFloat(JsonElement item)
        => item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var value) && float.IsFinite(value)
            ? value : throw new JsonException("Expected a finite numeric value.");
}
