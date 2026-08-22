using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    private const string CohereEmbeddingEndpoint = "v2/embed";
    private const string DefaultCohereEmbeddingInputType = "search_document";

    private static readonly HashSet<string> CohereEmbeddingInputTypes = new(StringComparer.Ordinal)
    {
        "search_document",
        "search_query",
        "classification",
        "clustering",
        "image"
    };

    private static readonly JsonSerializerOptions CohereEmbeddingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);

        var result = await SendCohereEmbeddingRequestAsync(
            request,
            DefaultCohereEmbeddingInputType,
            cancellationToken);

        return ToOpenAIEmbeddingResponse(result, request);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var inputType = ReadCohereInputType(request);
        var result = await SendCohereEmbeddingRequestAsync(openAIRequest, inputType, cancellationToken);
        var response = ToOpenAIEmbeddingResponse(result, openAIRequest);

        return new EmbeddingResponse
        {
            Embeddings = response.Data
                .OrderBy(item => item.Index)
                .Select(item => ReadCohereFloatEmbedding(item.Embedding, item.Index))
                .ToArray(),
            Usage = new EmbeddingUsage { Tokens = response.Usage.PromptTokens },
            Response = new EmbeddingResponseMetadata
            {
                Headers = result.Headers,
                Body = null
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                result.Response.Meta.ValueKind == JsonValueKind.Undefined
                    ? null
                    : result.Response.Meta),
            Warnings = ReadCohereWarnings(result.Response.Meta)
        };
    }

    private async Task<CohereEmbeddingResult> SendCohereEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        string inputType,
        CancellationToken cancellationToken)
    {
        var texts = ReadCohereTexts(request.Input);
        var embeddingType = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
            ? "base64"
            : "float";
        var payload = new CohereEmbeddingRequest
        {
            Model = request.Model,
            InputType = inputType,
            Texts = texts,
            OutputDimension = request.Dimensions,
            EmbeddingTypes = [embeddingType]
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, CohereEmbeddingEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CohereEmbeddingJsonOptions),
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
                    ? $"Cohere embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Cohere embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        CohereEmbeddingResponse parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CohereEmbeddingResponse>(raw, CohereEmbeddingJsonOptions)
                ?? throw new JsonException("The response body was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Cohere embedding request returned an invalid response.",
                exception);
        }

        return new CohereEmbeddingResult(parsed, response.GetHeaders());
    }

    private OpenAIEmbeddingResponse ToOpenAIEmbeddingResponse(
        CohereEmbeddingResult result,
        OpenAIEmbeddingRequest request)
    {
        var embeddingType = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
            ? "base64"
            : "float";
        if (!result.Response.Embeddings.TryGetProperty(embeddingType, out var embeddings)
            || embeddings.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Cohere embedding response did not contain '{embeddingType}' embeddings.");
        }

        var data = embeddings.EnumerateArray()
            .Select((embedding, index) => new OpenAIEmbeddingData
            {
                Index = index,
                Embedding = embedding.Clone()
            })
            .ToArray();
        var tokenCount = ReadCohereInputTokens(result.Response.Meta);

        return new OpenAIEmbeddingResponse
        {
            Data = data,
            Model = request.Model.ToModelId(GetIdentifier()),
            Usage = new OpenAIEmbeddingUsage
            {
                PromptTokens = tokenCount,
                TotalTokens = tokenCount
            }
        };
    }

    private string ReadCohereInputType(EmbeddingRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(options, "inputType", out var inputType))
        {
            return DefaultCohereEmbeddingInputType;
        }

        if (inputType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(inputType.GetString()))
        {
            throw new ArgumentException("providerOptions.cohere.inputType must be a non-empty string.", nameof(request));
        }

        var value = inputType.GetString()!;
        if (!CohereEmbeddingInputTypes.Contains(value))
        {
            throw new ArgumentException(
                "providerOptions.cohere.inputType must be one of: search_document, search_query, classification, clustering, image.",
                nameof(request));
        }

        if (string.Equals(value, "image", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "providerOptions.cohere.inputType 'image' is not supported by the text-only embedding contract.",
                nameof(request));
        }

        return value;
    }

    private static string[] ReadCohereTexts(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return [input.GetString()!];

        if (input.ValueKind == JsonValueKind.Array)
        {
            var items = input.EnumerateArray().ToArray();
            if (items.All(item => item.ValueKind == JsonValueKind.String))
                return items.Select(item => item.GetString()!).ToArray();
        }

        throw new ArgumentException(
            "Cohere embeddings support only string input or an array of strings; token arrays are not supported.",
            nameof(input));
    }

    private static IReadOnlyList<float> ReadCohereFloatEmbedding(JsonElement embedding, int index)
    {
        if (embedding.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Cohere embedding at index {index} was not returned in float format.");

        var values = new List<float>();
        foreach (var item in embedding.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var value))
                throw new InvalidOperationException($"Cohere embedding at index {index} contains a non-numeric value.");
            values.Add(value);
        }

        return values;
    }

    private static int ReadCohereInputTokens(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var sectionName in new[] { "billed_units", "tokens" })
        {
            if (TryGetPropertyIgnoreCase(meta, sectionName, out var section)
                && section.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(section, "input_tokens", out var inputTokens)
                && inputTokens.ValueKind == JsonValueKind.Number
                && inputTokens.TryGetDouble(out var value)
                && value >= 0
                && value <= int.MaxValue)
            {
                return (int)Math.Ceiling(value);
            }
        }

        return 0;
    }

    private static object[] ReadCohereWarnings(JsonElement meta)
    {
        if (meta.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(meta, "warnings", out var warnings)
            || warnings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return warnings.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => (object)item.GetString()!)
            .ToArray();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class CohereEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = null!;

        [JsonPropertyName("input_type")]
        public string InputType { get; init; } = null!;

        [JsonPropertyName("texts")]
        public string[] Texts { get; init; } = [];

        [JsonPropertyName("output_dimension")]
        public int? OutputDimension { get; init; }

        [JsonPropertyName("embedding_types")]
        public string[] EmbeddingTypes { get; init; } = [];
    }

    private sealed class CohereEmbeddingResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("embeddings")]
        public JsonElement Embeddings { get; init; }

        [JsonPropertyName("meta")]
        public JsonElement Meta { get; init; }
    }

    private sealed record CohereEmbeddingResult(
        CohereEmbeddingResponse Response,
        IDictionary<string, string> Headers);
}
