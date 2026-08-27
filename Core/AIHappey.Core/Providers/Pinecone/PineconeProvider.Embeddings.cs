using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;

namespace AIHappey.Core.Providers.Pinecone;

public partial class PineconeProvider
{
    private const string PineconeEmbeddingEndpoint = "embed";

    private static readonly JsonSerializerOptions PineconeEmbeddingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await SendPineconeEmbeddingRequestAsync(request, cancellationToken);
        return ToOpenAIEmbeddingResponse(result);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await SendPineconeEmbeddingRequestAsync(openAIRequest, cancellationToken);
        var response = ToOpenAIEmbeddingResponse(result);

        return new OpenAICompatibleEmbeddingResult(response, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()));
    }

    private async Task<PineconeEmbeddingResult> SendPineconeEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        var texts = ReadPineconeEmbeddingTexts(request);
        var parameters = ReadPineconeEmbeddingParameters(request);
        var payload = new PineconeEmbeddingPayload
        {
            Model = request.Model.Split('/').Last(),
            Parameters = parameters.Count == 0 ? null : parameters,
            Inputs = texts.Select(text => new PineconeEmbeddingInput { Text = text }).ToArray()
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, PineconeEmbeddingEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, PineconeEmbeddingJsonOptions),
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
                    ? $"Pinecone embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Pinecone embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            ValidatePineconeEmbeddingResponse(root);
            return new PineconeEmbeddingResult(root.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Pinecone embedding request returned an invalid response.",
                exception);
        }
    }

    private OpenAIEmbeddingResponse ToOpenAIEmbeddingResponse(PineconeEmbeddingResult result)
    {
        var root = result.Root;
        var data = root.GetProperty("data")
            .EnumerateArray()
            .Select((item, index) => new OpenAIEmbeddingData
            {
                Index = index,
                Embedding = item.GetProperty("values").Clone()
            })
            .ToArray();
        var tokens = root.GetProperty("usage").GetProperty("total_tokens").GetInt32();

        return new OpenAIEmbeddingResponse
        {
            Model = root.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
            Data = data,
            Usage = new OpenAIEmbeddingUsage
            {
                PromptTokens = tokens,
                TotalTokens = tokens
            }
        };
    }

    private static string[] ReadPineconeEmbeddingTexts(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Dimensions is not null)
            throw new ArgumentException("Pinecone embeddings do not support the OpenAI dimensions option.", nameof(request));
        if (request.EncodingFormat is not null
            && !string.Equals(request.EncodingFormat, "float", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Pinecone dense embeddings support only float encoding.", nameof(request));
        }

        if (request.Input.ValueKind == JsonValueKind.String)
        {
            var text = request.Input.GetString();
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Input cannot be empty.", nameof(request));
            return [text];
        }

        if (request.Input.ValueKind == JsonValueKind.Array)
        {
            var values = request.Input.EnumerateArray().ToArray();
            if (values.Length > 0
                && values.All(item => item.ValueKind == JsonValueKind.String)
                && values.All(item => !string.IsNullOrWhiteSpace(item.GetString())))
            {
                return values.Select(item => item.GetString()!).ToArray();
            }
        }

        throw new ArgumentException(
            "Pinecone embeddings support only a non-empty string or a non-empty array of strings.",
            nameof(request));
    }

    private static Dictionary<string, JsonElement> ReadPineconeEmbeddingParameters(OpenAIEmbeddingRequest request)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var properties = request.AdditionalProperties;

        if (properties is null)
            return result;

        if (TryGetValueIgnoreCase(properties, "parameters", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Pinecone embedding parameters must be an object.", nameof(request));

            foreach (var property in parameters.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        }

        if (TryGetValueIgnoreCase(properties, "inputType", out var inputType)
            || TryGetValueIgnoreCase(properties, "input_type", out inputType))
        {
            if (inputType.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(inputType.GetString()))
                throw new ArgumentException("Pinecone embedding inputType must be a non-empty string.", nameof(request));
            result["input_type"] = inputType.Clone();
        }

        if (TryGetValueIgnoreCase(properties, "truncate", out var truncate))
        {
            if (truncate.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(truncate.GetString()))
                throw new ArgumentException("Pinecone embedding truncate must be a non-empty string.", nameof(request));
            result["truncate"] = truncate.Clone();
        }

        return result;
    }

    private static void ValidatePineconeEmbeddingResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString())
            || !root.TryGetProperty("vector_type", out var vectorType)
            || vectorType.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("total_tokens", out var tokens)
            || !tokens.TryGetInt32(out var tokenCount)
            || tokenCount < 0)
        {
            throw new JsonException("The response did not contain the required Pinecone embedding fields.");
        }

        if (!string.Equals(vectorType.GetString(), "dense", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pinecone returned sparse embeddings, which are not supported by the dense embedding contract.");

        var index = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("values", out var values)
                || values.ValueKind != JsonValueKind.Array
                || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out _)))
            {
                throw new JsonException($"Pinecone embedding at index {index} did not contain a dense numeric values array.");
            }
            index++;
        }
    }

    private static bool TryGetValueIgnoreCase(
        Dictionary<string, JsonElement> values,
        string name,
        out JsonElement value)
    {
        foreach (var property in values)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class PineconeEmbeddingPayload
    {
        public string Model { get; init; } = null!;
        public Dictionary<string, JsonElement>? Parameters { get; init; }
        public PineconeEmbeddingInput[] Inputs { get; init; } = [];
    }

    private sealed class PineconeEmbeddingInput
    {
        public string Text { get; init; } = null!;
    }

    private sealed record PineconeEmbeddingResult(
        JsonElement Root,
        IDictionary<string, string> Headers);
}
