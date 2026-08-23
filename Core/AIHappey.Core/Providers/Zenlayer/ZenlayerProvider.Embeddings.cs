using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    private const string GeminiEmbeddingModel = "gemini-embedding-2";

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeZenlayerModelId(request.Model);

        if (!IsGeminiEmbeddingModel(model))
        {
            ApplyAuthHeader();
            request.Model = model;
            var result = await this.OpenAICompatibleEmbeddingRequestAsync(
                _client,
                request,
                endpoint: "v1/embeddings",
                cancellationToken: cancellationToken);
            return result.Response;
        }

        var results = await SendGeminiEmbeddingRequestsAsync(
            model,
            request.Input,
            request.AdditionalProperties,
            request.Dimensions,
            cancellationToken);

        return ToGeminiOpenAIResponse(results, model);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeZenlayerModelId(request.Model);

        if (!IsGeminiEmbeddingModel(model))
        {
            var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
            openAIRequest.Model = model;
            ApplyAuthHeader();
            var result = await this.OpenAICompatibleEmbeddingRequestAsync(
                _client,
                openAIRequest,
                endpoint: "v1/embeddings",
                cancellationToken: cancellationToken);

            return result.ToEmbeddingResponse(
                GetIdentifier().CreatePrimitiveProviderMetadata());
        }

        ValidateVercelEmbeddingRequest(request);
        var options = GetZenlayerProviderOptions(request.ProviderOptions);
        var input = JsonSerializer.SerializeToElement(request.Values.ToArray(), MediaJson);
        var results = await SendGeminiEmbeddingRequestsAsync(
            model,
            input,
            ToAdditionalProperties(options),
            dimensions: null,
            cancellationToken);
        var openAIResponse = ToGeminiOpenAIResponse(results, model);
        var headers = results.Count == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(results[0].Headers);

        return new OpenAICompatibleEmbeddingResult(openAIResponse, headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(
                results.Count == 1
                    ? results[0].Root.Clone()
                    : JsonSerializer.SerializeToElement(results.Select(result => result.Root.Clone()), MediaJson)));
    }

    private async Task<List<ZenlayerJsonResult>> SendGeminiEmbeddingRequestsAsync(
        string model,
        JsonElement input,
        Dictionary<string, JsonElement>? additionalProperties,
        int? dimensions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var basePayload = CreateOpenAIPayload(additionalProperties, "model", "input", "dimensions", "encoding_format", "user");
        if (dimensions is not null
            && !basePayload.ContainsKey("embedContentConfig"))
        {
            basePayload["embedContentConfig"] = new JsonObject
            {
                ["outputDimensionality"] = dimensions.Value
            };
        }

        var hasRawContent = basePayload.ContainsKey("content");
        var contents = hasRawContent ? [] : CreateGeminiContents(input);
        var results = new List<ZenlayerJsonResult>();

        if (hasRawContent)
        {
            results.Add(await SendJsonAsync(
                HttpMethod.Post,
                $"v1beta/models/{Uri.EscapeDataString(model)}:embedContent",
                basePayload,
                "Gemini embedding request",
                cancellationToken,
                googleApiKey: true));
            return results;
        }

        foreach (var content in contents)
        {
            var payload = (JsonObject)basePayload.DeepClone();
            payload["content"] = content;
            results.Add(await SendJsonAsync(
                HttpMethod.Post,
                $"v1beta/models/{Uri.EscapeDataString(model)}:embedContent",
                payload,
                "Gemini embedding request",
                cancellationToken,
                googleApiKey: true));
        }

        return results;
    }

    private static List<JsonNode> CreateGeminiContents(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return [CreateGeminiTextContent(input.GetString()!)];

        if (input.ValueKind == JsonValueKind.Object)
            return [NormalizeGeminiContent(input)];

        if (input.ValueKind != JsonValueKind.Array || input.GetArrayLength() == 0)
            throw new ArgumentException("Gemini embedding input must be non-empty text, content, or an array of those values.", nameof(input));

        return input.EnumerateArray().Select(item => item.ValueKind switch
        {
            JsonValueKind.String => (JsonNode)CreateGeminiTextContent(item.GetString()!),
            JsonValueKind.Object => NormalizeGeminiContent(item),
            _ => throw new ArgumentException("Gemini embedding array items must be text or content objects.", nameof(input))
        }).ToList();
    }

    private static JsonObject CreateGeminiTextContent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Gemini embedding input cannot be empty.", nameof(text));

        return new JsonObject
        {
            ["parts"] = new JsonArray(new JsonObject { ["text"] = text })
        };
    }

    private static JsonNode NormalizeGeminiContent(JsonElement content)
    {
        if (content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            return JsonNode.Parse(content.GetRawText())!;

        if (content.TryGetProperty("content", out var nested))
            return NormalizeGeminiContent(nested);

        throw new ArgumentException("Gemini multimodal embedding content must contain a parts array.", nameof(content));
    }

    private OpenAIEmbeddingResponse ToGeminiOpenAIResponse(
        IReadOnlyList<ZenlayerJsonResult> results,
        string model)
    {
        var data = new List<OpenAIEmbeddingData>(results.Count);
        var promptTokens = 0;
        var totalTokens = 0;

        for (var index = 0; index < results.Count; index++)
        {
            var root = results[index].Root;
            if (!root.TryGetProperty("embedding", out var embedding)
                || !embedding.TryGetProperty("values", out var values)
                || values.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Zenlayer Gemini embedding response did not contain embedding.values.");

            data.Add(new OpenAIEmbeddingData { Index = index, Embedding = values.Clone() });
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var prompt) && prompt.TryGetInt32(out var promptCount))
                    promptTokens += promptCount;
                if (usage.TryGetProperty("totalTokenCount", out var total) && total.TryGetInt32(out var totalCount))
                    totalTokens += totalCount;
            }
        }

        return new OpenAIEmbeddingResponse
        {
            Model = model.ToModelId(GetIdentifier()),
            Data = data,
            Usage = new OpenAIEmbeddingUsage
            {
                PromptTokens = promptTokens,
                TotalTokens = totalTokens == 0 ? promptTokens : totalTokens
            }
        };
    }

    private static Dictionary<string, JsonElement>? ToAdditionalProperties(JsonElement? options)
        => options is { ValueKind: JsonValueKind.Object } value
            ? value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
            : null;

    private JsonElement? GetZenlayerProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object
                ? options
                : null;

    private static void ValidateVercelEmbeddingRequest(EmbeddingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Values is null || !request.Values.Any())
            throw new ArgumentException("At least one value is required.", nameof(request));
    }

    private static bool IsGeminiEmbeddingModel(string model)
        => model.Equals(GeminiEmbeddingModel, StringComparison.OrdinalIgnoreCase);

    private string NormalizeZenlayerModelId(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }
}
