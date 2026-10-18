using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Nomic;

public partial class NomicProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await SendEmbeddingAsync(request, cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendEmbeddingAsync(request.ToOpenAIEmbeddingRequest(GetIdentifier()), cancellationToken);
        return new OpenAICompatibleEmbeddingResult(result.Response, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root));
    }

    private async Task<EmbeddingResult> SendEmbeddingAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        if (request.User is not null)
            throw new NotSupportedException("Nomic text embeddings do not support the user field.");

        string[] texts;
        if (request.Input.ValueKind == JsonValueKind.String)
            texts = [request.Input.GetString()!];
        else if (request.Input.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            texts = request.Input.EnumerateArray().Select(item => item.GetString()!).ToArray();
        else
            throw new NotSupportedException("Nomic text embeddings require strings, not token IDs.");

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Nomic API key.");

        var model = StripProviderPrefix(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in request.AdditionalProperties ?? [])
            payload[property.Key] = property.Value.Clone();
        payload["model"] = JsonSerializer.SerializeToElement(model);
        payload["texts"] = JsonSerializer.SerializeToElement(texts);
        if (request.Dimensions is not null)
            payload["dimensionality"] = JsonSerializer.SerializeToElement(request.Dimensions.Value);

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/embedding/text")
        {
            Content = JsonContent.Create(payload)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Nomic embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("embeddings", out var embeddings)
                || embeddings.ValueKind != JsonValueKind.Array
                || embeddings.GetArrayLength() != texts.Length)
                throw new JsonException("Expected one embedding per input text.");

            var data = embeddings.EnumerateArray().Select((embedding, index) =>
            {
                if (embedding.ValueKind != JsonValueKind.Array || embedding.GetArrayLength() == 0)
                    throw new JsonException("Expected a non-empty embedding vector.");
                var values = embedding.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var value) && float.IsFinite(value)
                        ? value : throw new JsonException("Expected finite float embedding values.")).ToArray();
                return new OpenAIEmbeddingData
                {
                    Index = index,
                    Embedding = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
                        ? JsonSerializer.SerializeToElement(EncodeBase64(values))
                        : embedding.Clone()
                };
            }).ToArray();

            var usage = root.TryGetProperty("usage", out var nativeUsage)
                ? nativeUsage.Deserialize<OpenAIEmbeddingUsage>() ?? new()
                : new OpenAIEmbeddingUsage();
            var returnedModel = root.TryGetProperty("model", out var modelProperty)
                && modelProperty.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(modelProperty.GetString())
                    ? modelProperty.GetString()! : model;
            return new EmbeddingResult(new OpenAIEmbeddingResponse
            {
                Model = $"{GetIdentifier()}/{StripProviderPrefix(returnedModel)}",
                Data = data,
                Usage = usage
            }, root.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Nomic embedding request returned an invalid response.", exception);
        }
    }

    private static string StripProviderPrefix(string model)
        => model.StartsWith("nomic/", StringComparison.OrdinalIgnoreCase) ? model["nomic/".Length..] : model;

    private static string EncodeBase64(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        return Convert.ToBase64String(bytes);
    }

    private sealed record EmbeddingResult(
        OpenAIEmbeddingResponse Response, JsonElement Root, IDictionary<string, string> Headers);
}
