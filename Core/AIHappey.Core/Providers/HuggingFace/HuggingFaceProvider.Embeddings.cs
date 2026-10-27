using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HuggingFace;

public partial class HuggingFaceProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await SendEmbeddingAsync(request, cancellationToken);
        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await SendEmbeddingAsync(openAIRequest, cancellationToken);

        return new EmbeddingResponse
        {
            Embeddings = result.Response.Data
                .OrderBy(item => item.Index)
                .Select(item => ReadFloatEmbedding(item.Embedding, item.Index))
                .ToArray(),
            Response = new EmbeddingResponseMetadata
            {
                Headers = result.Headers,
                Body = result.Root
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Warnings = []
        };
    }

    private async Task<HuggingFaceEmbeddingResult> SendEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);

        if (request.Dimensions is not null)
            throw new NotSupportedException("Hugging Face feature extraction does not support requested dimensions.");
        if (request.User is not null)
            throw new NotSupportedException("Hugging Face feature extraction does not support the user field.");

        var inputs = ReadInputTexts(request.Input);
        var model = StripProviderPrefix(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();

        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in request.AdditionalProperties ?? [])
            payload[property.Key] = property.Value.Clone();
        payload["inputs"] = request.Input.Clone();

        var encodedModel = string.Join("/", model.Split('/').Select(Uri.EscapeDataString));
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"hf-inference/models/{encodedModel}/pipeline/feature-extraction")
        {
            Content = JsonContent.Create(payload)
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
                    ? $"Hugging Face embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Hugging Face embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var embeddings = ReadEmbeddingVectors(document.RootElement, inputs.Length);
            var data = embeddings
                .Select((embedding, index) => new OpenAIEmbeddingData
                {
                    Index = index,
                    Embedding = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
                        ? JsonSerializer.SerializeToElement(EncodeBase64(ReadFloatEmbedding(embedding, index)))
                        : embedding
                })
                .ToArray();

            var providerModel = $"{GetIdentifier()}/{model}";
            return new HuggingFaceEmbeddingResult(
                new OpenAIEmbeddingResponse
                {
                    Model = providerModel,
                    Data = data,
                    Usage = new OpenAIEmbeddingUsage()
                },
                document.RootElement.Clone(),
                response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Hugging Face embedding request returned an invalid response.", exception);
        }
    }

    private static string[] ReadInputTexts(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return [input.GetString()!];

        if (input.ValueKind == JsonValueKind.Array
            && input.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
        {
            return input.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }

        throw new NotSupportedException("Hugging Face feature extraction requires string input or an array of strings.");
    }

    private static JsonElement[] ReadEmbeddingVectors(JsonElement root, int inputCount)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected an array of Hugging Face feature-extraction embeddings.");

        var items = root.EnumerateArray().ToArray();
        if (inputCount == 1 && items.All(item => item.ValueKind == JsonValueKind.Number))
            return [root.Clone()];

        if (items.Length != inputCount)
            throw new JsonException("Expected one Hugging Face embedding per input.");

        foreach (var (embedding, index) in items.Select((embedding, index) => (embedding, index)))
            _ = ReadFloatEmbedding(embedding, index);

        return items.Select(item => item.Clone()).ToArray();
    }

    private static IReadOnlyList<float> ReadFloatEmbedding(JsonElement embedding, int index)
    {
        if (embedding.ValueKind != JsonValueKind.Array || embedding.GetArrayLength() == 0)
            throw new JsonException($"Hugging Face embedding at index {index} was not a non-empty vector.");

        var values = new List<float>();
        foreach (var item in embedding.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetSingle(out var value)
                || !float.IsFinite(value))
            {
                throw new JsonException($"Hugging Face embedding at index {index} contains a non-numeric value.");
            }

            values.Add(value);
        }

        return values;
    }

    private static string EncodeBase64(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);

        return Convert.ToBase64String(bytes);
    }

    private static string StripProviderPrefix(string model)
        => model.StartsWith("huggingface/", StringComparison.OrdinalIgnoreCase)
            ? model["huggingface/".Length..]
            : model;

    private sealed record HuggingFaceEmbeddingResult(
        OpenAIEmbeddingResponse Response,
        JsonElement Root,
        IDictionary<string, string> Headers);
}
