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
        ArgumentNullException.ThrowIfNull(request);

        var nativeOptions = ReadCohereNativeEmbeddingOptions(request.AdditionalProperties);
        ValidateCohereOpenAIEmbeddingRequest(request, nativeOptions);

        var result = await SendCohereEmbeddingRequestAsync(
            request,
            ReadCohereInputType(request.AdditionalProperties),
            nativeOptions,
            cancellationToken);

        return ToOpenAIEmbeddingResponse(result, request);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var providerOptions = ReadCohereProviderOptions(request.ProviderOptions);
        var nativeOptions = ReadCohereNativeEmbeddingOptions(providerOptions);
        var openAIRequest = nativeOptions.HasNativeInput
            ? CreateNativeInputEmbeddingRequest(request.Model, providerOptions)
            : request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var inputType = ReadCohereInputType(providerOptions);
        var result = await SendCohereEmbeddingRequestAsync(
            openAIRequest,
            inputType,
            nativeOptions,
            cancellationToken);
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
        CohereNativeEmbeddingOptions nativeOptions,
        CancellationToken cancellationToken)
    {
        var embeddingType = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
            ? "base64"
            : "float";
        var payload = new CohereEmbeddingRequest
        {
            Model = request.Model,
            InputType = inputType,
            Texts = nativeOptions.HasNativeInput ? null : ReadCohereTexts(request.Input),
            Inputs = nativeOptions.Inputs,
            Images = nativeOptions.Images,
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

    private static string ReadCohereInputType(Dictionary<string, JsonElement>? properties)
    {
        if (properties is null
            || !TryGetPropertyIgnoreCase(properties, "input_type", out var inputType)
            && !TryGetPropertyIgnoreCase(properties, "inputType", out inputType))
        {
            return DefaultCohereEmbeddingInputType;
        }

        return ReadCohereInputType(inputType);
    }

    private static string ReadCohereInputType(JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Object } providerOptions
            || !TryGetPropertyIgnoreCase(providerOptions, "inputType", out var inputType)
            && !TryGetPropertyIgnoreCase(providerOptions, "input_type", out inputType))
        {
            return DefaultCohereEmbeddingInputType;
        }

        return ReadCohereInputType(inputType);
    }

    private static string ReadCohereInputType(JsonElement inputType)
    {
        if (inputType.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(inputType.GetString()))
            throw new ArgumentException("providerOptions.cohere.inputType must be a non-empty string.", nameof(inputType));

        var value = inputType.GetString()!;
        if (!CohereEmbeddingInputTypes.Contains(value))
        {
            throw new ArgumentException(
                "providerOptions.cohere.inputType must be one of: search_document, search_query, classification, clustering, image.",
                nameof(inputType));
        }

        return value;
    }

    private static JsonElement? ReadCohereProviderOptions(
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return null;

        foreach (var (key, value) in providerOptions)
        {
            if (string.Equals(key, "cohere", StringComparison.OrdinalIgnoreCase))
                return value.ValueKind == JsonValueKind.Object ? value : null;
        }

        return null;
    }

    private static CohereNativeEmbeddingOptions ReadCohereNativeEmbeddingOptions(
        Dictionary<string, JsonElement>? properties)
    {
        if (properties is null)
            return new(null, null);

        return new(
            ReadCohereNativeInput(properties, "inputs"),
            ReadCohereNativeInput(properties, "images"));
    }

    private static CohereNativeEmbeddingOptions ReadCohereNativeEmbeddingOptions(JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Object } providerOptions)
            return new(null, null);

        return new(
            ReadCohereNativeInput(providerOptions, "inputs"),
            ReadCohereNativeInput(providerOptions, "images"));
    }

    private static JsonElement? ReadCohereNativeInput(
        Dictionary<string, JsonElement> properties,
        string propertyName)
    {
        foreach (var (key, value) in properties)
        {
            if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                return ValidateCohereNativeInput(value, propertyName);
        }

        return null;
    }

    private static JsonElement? ReadCohereNativeInput(JsonElement options, string propertyName)
        => TryGetPropertyIgnoreCase(options, propertyName, out var value)
            ? ValidateCohereNativeInput(value, propertyName)
            : null;

    private static JsonElement ValidateCohereNativeInput(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Array || !value.EnumerateArray().Any())
            throw new ArgumentException($"providerOptions.cohere.{propertyName} must be a non-empty array.", nameof(value));

        return value.Clone();
    }

    private static OpenAIEmbeddingRequest CreateNativeInputEmbeddingRequest(
        string model,
        JsonElement? providerOptions)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        int? dimensions = null;
        if (providerOptions is { ValueKind: JsonValueKind.Object } options
            && TryGetPropertyIgnoreCase(options, "dimensions", out var dimensionsElement))
        {
            if (!dimensionsElement.TryGetInt32(out var value) || value <= 0)
                throw new ArgumentException("providerOptions.cohere.dimensions must be a positive integer.", nameof(providerOptions));

            dimensions = value;
        }

        return new OpenAIEmbeddingRequest
        {
            Model = model,
            Dimensions = dimensions,
            EncodingFormat = "float"
        };
    }

    private static void ValidateCohereOpenAIEmbeddingRequest(
        OpenAIEmbeddingRequest request,
        CohereNativeEmbeddingOptions nativeOptions)
    {
        if (!nativeOptions.HasNativeInput)
        {
            ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
            return;
        }

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

    private static bool TryGetPropertyIgnoreCase(
        Dictionary<string, JsonElement> properties,
        string name,
        out JsonElement value)
    {
        foreach (var (key, propertyValue) in properties)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = propertyValue;
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
        public string[]? Texts { get; init; }

        [JsonPropertyName("inputs")]
        public JsonElement? Inputs { get; init; }

        [JsonPropertyName("images")]
        public JsonElement? Images { get; init; }

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

    private sealed record CohereNativeEmbeddingOptions(
        JsonElement? Inputs,
        JsonElement? Images)
    {
        public bool HasNativeInput => Inputs is not null || Images is not null;
    }
}
