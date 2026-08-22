using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>
/// Transport, validation, and mapping helpers for OpenAI-compatible embedding endpoints.
/// </summary>
public static class ModelProviderEmbeddingCompatibilityExtensions
{
    private static readonly JsonSerializerOptions EmbeddingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<OpenAICompatibleEmbeddingResult>
        OpenAICompatibleEmbeddingRequestAsync(
            this IModelProvider modelProvider,
            HttpClient httpClient,
            OpenAIEmbeddingRequest options,
            string? endpoint = "v1/embeddings",
            CancellationToken cancellationToken = default)
        => await SendOpenAICompatibleEmbeddingRequestAsync(
            httpClient,
            options,
            modelProvider.GetIdentifier(),
            endpoint,
            cancellationToken);

    /// <summary>
    /// Provider-neutral overload used by transport tests and callers that do not
    /// need gateway model-id qualification.
    /// </summary>
    public static async Task<OpenAICompatibleEmbeddingResult>
        OpenAICompatibleEmbeddingRequestAsync(
            this HttpClient httpClient,
            OpenAIEmbeddingRequest options,
            string? endpoint = "v1/embeddings",
            CancellationToken cancellationToken = default)
        => await SendOpenAICompatibleEmbeddingRequestAsync(
            httpClient,
            options,
            providerIdentifier: null,
            endpoint,
            cancellationToken);

    private static async Task<OpenAICompatibleEmbeddingResult> SendOpenAICompatibleEmbeddingRequestAsync(
        HttpClient httpClient,
        OpenAIEmbeddingRequest options,
        string? providerIdentifier,
        string? endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateOpenAIEmbeddingRequest(options);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(options, EmbeddingJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"Embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        OpenAIEmbeddingResponse result;
        try
        {
            result = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(raw, EmbeddingJsonOptions)
                ?? throw new JsonException("The response body was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Embedding request returned an invalid OpenAI-compatible response.",
                exception);
        }

        if (!string.IsNullOrWhiteSpace(providerIdentifier))
            result.Model = result.Model.ToModelId(providerIdentifier);

        return new OpenAICompatibleEmbeddingResult(result, response.GetHeaders());
    }

    public static OpenAIEmbeddingRequest ToOpenAIEmbeddingRequest(
        this EmbeddingRequest request,
        string providerIdentifier)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var values = request.Values?.ToArray() ?? [];
        if (values.Length == 0)
            throw new ArgumentException("At least one value is required.", nameof(request));
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Embedding values cannot be empty.", nameof(request));

        var result = new OpenAIEmbeddingRequest
        {
            Model = request.Model,
            Input = JsonSerializer.SerializeToElement(values, EmbeddingJsonOptions),
            EncodingFormat = "float"
        };

        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(providerIdentifier, out var options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (TryGetPropertyIgnoreCase(options, "dimensions", out var dimensions))
        {
            if (!dimensions.TryGetInt32(out var value) || value <= 0)
                throw new ArgumentException($"providerOptions.{providerIdentifier}.dimensions must be a positive integer.", nameof(request));

            result.Dimensions = value;
        }

        if (TryGetPropertyIgnoreCase(options, "user", out var user))
        {
            if (user.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(user.GetString()))
                throw new ArgumentException($"providerOptions.{providerIdentifier}.user must be a non-empty string.", nameof(request));

            result.User = user.GetString();
        }

        // Vercel providerOptions are provider-keyed. Once the matching provider has
        // been selected, forward its remaining properties as native top-level fields.
        // OpenAI-compatible requests already arrive with unkeyed JsonExtensionData and
        // therefore do not pass through this conversion.
        foreach (var property in options.EnumerateObject())
        {
            if (IsCanonicalEmbeddingProperty(property.Name))
                continue;

            (result.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase))
                [property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static bool IsCanonicalEmbeddingProperty(string name)
        => name.Equals("input", StringComparison.OrdinalIgnoreCase)
            || name.Equals("model", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
            || name.Equals("encoding_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("encodingFormat", StringComparison.OrdinalIgnoreCase)
            || name.Equals("user", StringComparison.OrdinalIgnoreCase);

    public static EmbeddingResponse ToEmbeddingResponse(
        this OpenAICompatibleEmbeddingResult result,
        Dictionary<string, JsonElement>? providerMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var embeddings = result.Response.Data
            .OrderBy(item => item.Index)
            .Select(item => ReadFloatEmbedding(item.Embedding, item.Index))
            .ToArray();

        return new EmbeddingResponse
        {
            Embeddings = embeddings,
            Usage = new EmbeddingUsage { Tokens = result.Response.Usage.PromptTokens },
            Response = new EmbeddingResponseMetadata
            {
                Headers = result.Headers,
                Body = null
            },
            ProviderMetadata = providerMetadata,
            Warnings = []
        };
    }

    public static void ValidateOpenAIEmbeddingRequest(OpenAIEmbeddingRequest request)
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

        ValidateInput(request.Input, nameof(request));
    }

    private static void ValidateInput(JsonElement input, string parameterName)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            if (string.IsNullOrWhiteSpace(input.GetString()))
                throw new ArgumentException("Input cannot be empty.", parameterName);
            return;
        }

        if (input.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Input must be a string, an array of strings, an array of integer tokens, or an array of integer token arrays.", parameterName);

        var items = input.EnumerateArray().ToArray();
        if (items.Length == 0)
            throw new ArgumentException("Input cannot be an empty array.", parameterName);

        if (items.All(item => item.ValueKind == JsonValueKind.String))
        {
            if (items.Any(item => string.IsNullOrWhiteSpace(item.GetString())))
                throw new ArgumentException("Input strings cannot be empty.", parameterName);
            return;
        }

        if (items.All(IsInteger))
            return;

        if (items.All(item => item.ValueKind == JsonValueKind.Array))
        {
            foreach (var tokenArray in items)
            {
                var tokens = tokenArray.EnumerateArray().ToArray();
                if (tokens.Length == 0 || tokens.Any(token => !IsInteger(token)))
                    throw new ArgumentException("Every token array must contain one or more integers.", parameterName);
            }
            return;
        }

        throw new ArgumentException("Input must contain only strings, only integer tokens, or only integer token arrays.", parameterName);
    }

    private static bool IsInteger(JsonElement element)
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _);

    private static IReadOnlyList<float> ReadFloatEmbedding(JsonElement embedding, int index)
    {
        if (embedding.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Embedding at index {index} was not returned in float format.");

        var values = new List<float>();
        foreach (var item in embedding.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var value))
                throw new InvalidOperationException($"Embedding at index {index} contains a non-numeric value.");
            values.Add(value);
        }

        return values;
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
}

public sealed record OpenAICompatibleEmbeddingResult(
    OpenAIEmbeddingResponse Response,
    IDictionary<string, string> Headers);
