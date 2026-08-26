using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider
{
    private const string JinaEmbeddingEndpoint = "https://api.jina.ai/v1/embeddings";

    private static readonly JsonSerializerOptions JinaEmbeddingJson = new(JsonSerializerDefaults.Web);

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ValidateJinaEmbeddingRequest(request);

        var result = await SendJinaEmbeddingAsync(request, forceFloat: false, cancellationToken);
        return ToJinaOpenAIResponse(result.Root, request.Model);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await SendJinaEmbeddingAsync(openAIRequest, forceFloat: true, cancellationToken);
        var response = ToJinaOpenAIResponse(result.Root, openAIRequest.Model);

        return new OpenAICompatibleEmbeddingResult(response, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()));
    }

    private async Task<JinaEmbeddingResult> SendJinaEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        bool forceFloat,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        // Jina's request shapes vary by model generation. Preserve native options and
        // multimodal/document inputs instead of imposing a provider-specific DTO.
        foreach (var property in request.AdditionalProperties ?? [])
        {
            if (property.Key.Equals("input", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("encoding_format", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload[property.Key] = property.Value.Clone();
        }

        payload["model"] = JsonSerializer.SerializeToElement(request.Model.Split('/').Last());
        payload["input"] = request.Input.Clone();

        if (request.Dimensions is not null)
            payload["dimensions"] = JsonSerializer.SerializeToElement(request.Dimensions.Value);

        if (forceFloat)
        {
            payload["embedding_type"] = JsonSerializer.SerializeToElement("float");
        }
        else if (!string.IsNullOrWhiteSpace(request.EncodingFormat))
        {
            payload["embedding_type"] = JsonSerializer.SerializeToElement(
                request.EncodingFormat.ToLowerInvariant());
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, JinaEmbeddingEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JinaEmbeddingJson),
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
                    ? $"Jina embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Jina embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The response did not contain a data array.");
            }

            return new JinaEmbeddingResult(root.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Jina embedding request returned an invalid response.",
                exception);
        }
    }

    private OpenAIEmbeddingResponse ToJinaOpenAIResponse(JsonElement root, string requestedModel)
    {
        var data = root.GetProperty("data")
            .EnumerateArray()
            .Select((item, position) =>
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("embedding", out var embedding)
                    || embedding.ValueKind is not (JsonValueKind.Array or JsonValueKind.String))
                {
                    throw new InvalidOperationException(
                        $"Jina embedding result at position {position} did not contain an embedding.");
                }

                var index = item.TryGetProperty("index", out var indexElement)
                    && indexElement.TryGetInt32(out var nativeIndex)
                        ? nativeIndex
                        : position;

                return new OpenAIEmbeddingData
                {
                    Index = index,
                    Embedding = embedding.Clone()
                };
            })
            .ToArray();

        var promptTokens = ReadJinaTokenCount(root, "prompt_tokens");
        var totalTokens = ReadJinaTokenCount(root, "total_tokens");
        if (promptTokens == 0)
            promptTokens = totalTokens;
        if (totalTokens == 0)
            totalTokens = promptTokens;

        var model = root.TryGetProperty("model", out var modelElement)
            && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : requestedModel;

        return new OpenAIEmbeddingResponse
        {
            Model = (model ?? requestedModel).ToModelId(GetIdentifier()),
            Data = data,
            Usage = new OpenAIEmbeddingUsage
            {
                PromptTokens = promptTokens,
                TotalTokens = totalTokens
            }
        };
    }

    private static int ReadJinaTokenCount(JsonElement root, string propertyName)
        => root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty(propertyName, out var tokens)
            && tokens.TryGetInt32(out var count)
                ? count
                : 0;

    private static void ValidateJinaEmbeddingRequest(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Dimensions is <= 0)
            throw new ArgumentException("Dimensions must be a positive integer.", nameof(request));
        if (request.EncodingFormat is not null
            && !string.Equals(request.EncodingFormat, "float", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Encoding format must be either 'float' or 'base64'.", nameof(request));
        }

        if (request.Input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new ArgumentException("Input is required.", nameof(request));
        if (request.Input.ValueKind == JsonValueKind.String
            && string.IsNullOrWhiteSpace(request.Input.GetString()))
            throw new ArgumentException("Input cannot be empty.", nameof(request));
        if (request.Input.ValueKind == JsonValueKind.Array
            && !request.Input.EnumerateArray().Any())
            throw new ArgumentException("Input cannot be an empty array.", nameof(request));
    }

    private sealed record JinaEmbeddingResult(
        JsonElement Root,
        IDictionary<string, string> Headers);
}
