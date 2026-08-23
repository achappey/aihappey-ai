using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZeroEntropy;

public partial class ZeroEntropyProvider
{
    private const string ZeroEntropyEmbeddingEndpoint = "v1/models/embed";

    private static readonly JsonSerializerOptions ZeroEntropyEmbeddingJson = new(JsonSerializerDefaults.Web);

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);

        var result = await SendZeroEntropyEmbeddingAsync(request, cancellationToken);
        return ToZeroEntropyOpenAIResponse(result.Root, request.Model);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await SendZeroEntropyEmbeddingAsync(openAIRequest, cancellationToken);
        var response = ToZeroEntropyOpenAIResponse(result.Root, openAIRequest.Model);

        return new OpenAICompatibleEmbeddingResult(response, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()));
    }

    private async Task<ZeroEntropyEmbeddingResult> SendZeroEntropyEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        // Both OpenAI extension data and Vercel providerOptions are already represented
        // as native top-level fields here. Preserve them without imposing a provider DTO.
        foreach (var property in request.AdditionalProperties ?? [])
            payload[property.Key] = property.Value.Clone();

        payload["model"] = JsonSerializer.SerializeToElement(request.Model.Split('/').Last());
        payload["input"] = request.Input.Clone();

        if (request.Dimensions is not null)
            payload["dimensions"] = JsonSerializer.SerializeToElement(request.Dimensions.Value);
        if (!string.IsNullOrWhiteSpace(request.EncodingFormat))
            payload["encoding_format"] = JsonSerializer.SerializeToElement(request.EncodingFormat);
        if (!payload.ContainsKey("input_type"))
            payload["input_type"] = JsonSerializer.SerializeToElement("document");

        using var message = new HttpRequestMessage(HttpMethod.Post, ZeroEntropyEmbeddingEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ZeroEntropyEmbeddingJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"ZeroEntropy embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"ZeroEntropy embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The response did not contain a results array.");
            }

            return new ZeroEntropyEmbeddingResult(root.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "ZeroEntropy embedding request returned an invalid response.",
                exception);
        }
    }

    private OpenAIEmbeddingResponse ToZeroEntropyOpenAIResponse(JsonElement root, string requestedModel)
    {
        var data = root.GetProperty("results")
            .EnumerateArray()
            .Select((result, index) =>
            {
                if (result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("embedding", out var embedding)
                    || embedding.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
                {
                    throw new InvalidOperationException(
                        $"ZeroEntropy embedding result at index {index} did not contain an embedding.");
                }

                return new OpenAIEmbeddingData
                {
                    Index = index,
                    Embedding = embedding.Clone()
                };
            })
            .ToArray();

        var totalTokens = root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("total_tokens", out var tokens)
            && tokens.TryGetInt32(out var count)
                ? count
                : 0;

        return new OpenAIEmbeddingResponse
        {
            Model = requestedModel.ToModelId(GetIdentifier()),
            Data = data,
            Usage = new OpenAIEmbeddingUsage
            {
                PromptTokens = totalTokens,
                TotalTokens = totalTokens
            }
        };
    }

    private sealed record ZeroEntropyEmbeddingResult(
        JsonElement Root,
        IDictionary<string, string> Headers);
}
