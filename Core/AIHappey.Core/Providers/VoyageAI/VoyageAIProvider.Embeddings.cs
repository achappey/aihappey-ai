using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.VoyageAI;

public partial class VoyageAIProvider
{
    private static readonly JsonSerializerOptions VoyageEmbeddingJson = new(JsonSerializerDefaults.Web);

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        var result = await SendVoyageEmbeddingAsync(request, cancellationToken);
        return ToVoyageOpenAIResponse(result.Root, request.Model);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await SendVoyageEmbeddingAsync(openAIRequest, cancellationToken);
        var response = ToVoyageOpenAIResponse(result.Root, openAIRequest.Model);

        return new OpenAICompatibleEmbeddingResult(response, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()));
    }

    private async Task<VoyageEmbeddingResult> SendVoyageEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        var model = request.Model.Split('/').Last();
        var isMultimodal = model.StartsWith("voyage-multimodal-", StringComparison.OrdinalIgnoreCase);
        var isContextual = model.StartsWith("voyage-context-", StringComparison.OrdinalIgnoreCase);
        var endpoint = isMultimodal
            ? "v1/multimodalembeddings"
            : isContextual ? "v1/contextualizedembeddings" : "v1/embeddings";

        var payload = CopyVoyageOptions(request.AdditionalProperties);
        payload["model"] = model;

        if (request.Dimensions is not null && !payload.ContainsKey("output_dimension"))
            payload["output_dimension"] = request.Dimensions.Value;
        if (string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase))
            payload[isMultimodal ? "output_encoding" : "encoding_format"] = "base64";

        if (isContextual)
        {
            if (!payload.ContainsKey("inputs"))
                payload["inputs"] = ToContextualInputs(request.Input);
        }
        else if (isMultimodal)
        {
            payload["inputs"] = ToVoyageMultimodalInputs(request.Input);
        }
        else
        {
            payload["input"] = RequireTextInput(request.Input, "Voyage text embeddings");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(VoyageEmbeddingJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Voyage embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new VoyageEmbeddingResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Voyage embedding request returned invalid JSON.", exception);
        }
    }

    private static JsonObject CopyVoyageOptions(Dictionary<string, JsonElement>? additional)
    {
        var payload = new JsonObject();
        foreach (var property in additional ?? [])
        {
            if (property.Key.Equals("input", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("encoding_format", StringComparison.OrdinalIgnoreCase))
                continue;
            payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
        }
        return payload;
    }

    private static JsonNode RequireTextInput(JsonElement input, string provider)
    {
        if (input.ValueKind == JsonValueKind.String)
            return JsonValue.Create(input.GetString())!;
        if (input.ValueKind == JsonValueKind.Array && input.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
            return JsonNode.Parse(input.GetRawText())!;
        throw new ArgumentException($"{provider} accepts only a string or an array of strings on this endpoint.", nameof(input));
    }

    private static JsonNode ToContextualInputs(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return new JsonArray(new JsonArray(input.GetString()));
        if (input.ValueKind == JsonValueKind.Array && input.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
            return new JsonArray(input.EnumerateArray().Select(x => (JsonNode?)new JsonArray(x.GetString())).ToArray());
        if (input.ValueKind == JsonValueKind.Array
            && input.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Array
                && x.EnumerateArray().All(y => y.ValueKind == JsonValueKind.String)))
            return JsonNode.Parse(input.GetRawText())!;
        throw new ArgumentException("Voyage contextual embeddings require a string, strings, or nested string arrays.", nameof(input));
    }

    private static JsonNode ToVoyageMultimodalInputs(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = input.GetString() }) });

        if (input.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Voyage multimodal input must be text or an array of text/content objects.", nameof(input));

        var items = input.EnumerateArray().ToArray();
        if (items.All(x => x.ValueKind == JsonValueKind.String))
            return new JsonArray(items.Select(x => (JsonNode?)new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = x.GetString() })
            }).ToArray());

        var inputs = new JsonArray();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Each Voyage multimodal input must contain a content array.", nameof(input));
            var converted = new JsonArray(content.EnumerateArray().Select(ConvertVoyagePart).ToArray());
            inputs.Add(new JsonObject { ["content"] = converted });
        }
        return inputs;
    }

    private static JsonNode ConvertVoyagePart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Voyage multimodal content parts must be objects.");
        var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type == "text")
            return new JsonObject { ["type"] = "text", ["text"] = part.GetProperty("text").GetString() };

        if (type == "image_url")
        {
            var value = ReadMediaValue(part.GetProperty("image_url"));
            return CreateVoyageMediaPart("image", value);
        }
        if (type == "input_video")
        {
            var value = ReadMediaValue(part.GetProperty("input_video"));
            return CreateVoyageMediaPart("video", value);
        }
        if (type is "image_base64" or "video_url" or "video_base64")
            return JsonNode.Parse(part.GetRawText())!;

        throw new ArgumentException($"Voyage does not support multimodal content part type '{type}'.");
    }

    private static string ReadMediaValue(JsonElement media)
    {
        if (media.ValueKind == JsonValueKind.String)
            return media.GetString()!;
        if (media.ValueKind == JsonValueKind.Object)
        {
            if (media.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString()!;
            if (media.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                return data.GetString()!;
        }
        throw new ArgumentException("Multimodal media requires a URL or data URI.");
    }

    private static JsonNode CreateVoyageMediaPart(string kind, string value)
    {
        var suffix = value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "base64" : "url";
        var key = $"{kind}_{suffix}";
        return new JsonObject { ["type"] = key, [key] = value };
    }

    private OpenAIEmbeddingResponse ToVoyageOpenAIResponse(JsonElement root, string requestedModel)
    {
        var data = new List<OpenAIEmbeddingData>();
        if (root.TryGetProperty("data", out var outer) && outer.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outer.EnumerateArray())
            {
                if (item.TryGetProperty("embedding", out var embedding))
                    data.Add(new OpenAIEmbeddingData { Index = data.Count, Embedding = embedding.Clone() });
                else if (item.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Array)
                    foreach (var chunk in nested.EnumerateArray())
                        if (chunk.TryGetProperty("embedding", out var chunkEmbedding))
                            data.Add(new OpenAIEmbeddingData { Index = data.Count, Embedding = chunkEmbedding.Clone() });
            }
        }

        var tokens = root.TryGetProperty("usage", out var usage)
            && usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var count) ? count : 0;
        var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : requestedModel;
        return new OpenAIEmbeddingResponse
        {
            Model = (model ?? requestedModel).ToModelId(GetIdentifier()),
            Data = data,
            Usage = new OpenAIEmbeddingUsage { PromptTokens = tokens, TotalTokens = tokens }
        };
    }

    private sealed record VoyageEmbeddingResult(JsonElement Root, IDictionary<string, string> Headers);
}
