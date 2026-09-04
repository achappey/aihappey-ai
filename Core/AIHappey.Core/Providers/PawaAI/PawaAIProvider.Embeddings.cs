using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.PawaAI;

public partial class PawaAIProvider
{
    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = request.Values?.ToArray() ?? [];
        var result = await CreatePawaEmbeddingsAsync(
            request.Model,
            values,
            GetPawaOptions(request.ProviderOptions),
            cancellationToken);

        return new EmbeddingResponse
        {
            Embeddings = result.Embeddings,
            Usage = null,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new EmbeddingResponseMetadata { Headers = result.Headers, Body = result.Root },
            Warnings = []
        };
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = ReadPawaEmbeddingInput(request.Input);
        var options = request.AdditionalProperties is null
            ? default
            : JsonSerializer.SerializeToElement(request.AdditionalProperties, PawaJson);
        var result = await CreatePawaEmbeddingsAsync(request.Model, values, options, cancellationToken);

        return new OpenAIEmbeddingResponse
        {
            Model = request.Model.ToModelId(GetIdentifier()),
            Data = result.Embeddings.Select((embedding, index) => new OpenAIEmbeddingData
            {
                Index = index,
                Embedding = JsonSerializer.SerializeToElement(embedding, PawaJson)
            }),
            Usage = new OpenAIEmbeddingUsage()
        };
    }

    private async Task<PawaEmbeddingResult> CreatePawaEmbeddingsAsync(
        string model,
        IReadOnlyCollection<string> values,
        JsonElement options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty embedding value is required.", nameof(values));

        var payload = CopyPawaOptions(options);
        payload["model"] = NormalizePawaModelId(model);
        payload["sentences"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray<JsonNode?>());
        if (!payload.ContainsKey("lang"))
            payload["lang"] = "multi";

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/vectors/embedding")
        {
            Content = new StringContent(payload.ToJsonString(PawaJson), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsurePawaSuccess(response, raw, "embedding request");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("embeddings", out var embeddings)
            || embeddings.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("PawaAI embedding response did not contain data.embeddings.");

        var vectors = embeddings.EnumerateArray()
            .Select(vector => vector.EnumerateArray().Select(number => number.GetSingle()).ToArray())
            .ToArray();
        return new PawaEmbeddingResult(vectors, root, response.GetHeaders());
    }

    private static string[] ReadPawaEmbeddingInput(JsonElement input)
        => input.ValueKind switch
        {
            JsonValueKind.String when !string.IsNullOrWhiteSpace(input.GetString()) => [input.GetString()!],
            JsonValueKind.Array => input.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray(),
            _ => throw new ArgumentException("PawaAI embeddings support string input or an array of strings.", nameof(input))
        };

    private sealed record PawaEmbeddingResult(
        IReadOnlyList<float[]> Embeddings,
        JsonElement Root,
        IDictionary<string, string> Headers);
}
