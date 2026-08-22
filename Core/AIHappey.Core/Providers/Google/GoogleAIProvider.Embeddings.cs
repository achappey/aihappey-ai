using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static readonly JsonSerializerOptions GoogleEmbeddingJson = new(JsonSerializerDefaults.Web);

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ValidateGoogleEmbeddingRequest(request);
        var result = await SendGoogleEmbeddingAsync(request, cancellationToken);
        return ToGoogleOpenAIResponse(result.Root, request.Model);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await SendGoogleEmbeddingAsync(openAIRequest, cancellationToken);
        var response = ToGoogleOpenAIResponse(result.Root, openAIRequest.Model);
        return new OpenAICompatibleEmbeddingResult(response, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()));
    }

    private async Task<GoogleEmbeddingResult> SendGoogleEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        var model = request.Model.Split('/').Last();
        var contents = ToGoogleContents(request.Input);
        var common = CopyGoogleOptions(request.AdditionalProperties);
        common["model"] = $"models/{model}";
        if (request.Dimensions is not null && !common.ContainsKey("outputDimensionality")
            && !common.ContainsKey("output_dimensionality"))
            common["outputDimensionality"] = request.Dimensions.Value;

        string endpoint;
        JsonNode payload;
        if (contents.Count == 1)
        {
            endpoint = $"v1beta/models/{Uri.EscapeDataString(model)}:embedContent";
            common["content"] = contents[0]?.DeepClone();
            payload = common;
        }
        else
        {
            endpoint = $"v1beta/models/{Uri.EscapeDataString(model)}:batchEmbedContents";
            var requests = new JsonArray();
            foreach (var content in contents)
            {
                var item = (JsonObject)common.DeepClone();
                item["content"] = content?.DeepClone();
                requests.Add(item);
            }
            payload = new JsonObject { ["requests"] = requests };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(GoogleEmbeddingJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new GoogleEmbeddingResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Google embedding request returned invalid JSON.", exception);
        }
    }

    private static void ValidateGoogleEmbeddingRequest(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Dimensions is <= 0)
            throw new ArgumentException("Dimensions must be a positive integer.", nameof(request));
        if (string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Google embeddings do not return OpenAI base64-encoded vectors.", nameof(request));
    }

    private static JsonObject CopyGoogleOptions(Dictionary<string, JsonElement>? additional)
    {
        var result = new JsonObject();
        foreach (var property in additional ?? [])
        {
            if (property.Key.Equals("input", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("encoding_format", StringComparison.OrdinalIgnoreCase))
                continue;
            result[property.Key] = JsonNode.Parse(property.Value.GetRawText());
        }
        return result;
    }

    private static JsonArray ToGoogleContents(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return new JsonArray(CreateGoogleTextContent(input.GetString()!));
        if (input.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Google embedding input must be text or an array of text/content objects.", nameof(input));

        var items = input.EnumerateArray().ToArray();
        if (items.Length == 0)
            throw new ArgumentException("Google embedding input cannot be empty.", nameof(input));
        if (items.All(x => x.ValueKind == JsonValueKind.String))
            return new JsonArray(items.Select(x => (JsonNode?)CreateGoogleTextContent(x.GetString()!)).ToArray());

        var contents = new JsonArray();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Google multimodal embedding items must be content objects.", nameof(input));
            if (item.TryGetProperty("parts", out var nativeParts) && nativeParts.ValueKind == JsonValueKind.Array)
            {
                contents.Add(JsonNode.Parse(item.GetRawText()));
                continue;
            }
            if (!item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Google multimodal embedding items require a content array.", nameof(input));
            contents.Add(new JsonObject { ["parts"] = new JsonArray(parts.EnumerateArray().Select(ConvertGooglePart).ToArray()) });
        }
        return contents;
    }

    private static JsonObject CreateGoogleTextContent(string text)
        => new() { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) };

    private static JsonNode ConvertGooglePart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Google content parts must be objects.");
        if (part.TryGetProperty("text", out var nativeText) && !part.TryGetProperty("type", out _))
            return JsonNode.Parse(part.GetRawText())!;

        var type = part.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type == "text")
            return new JsonObject { ["text"] = part.GetProperty("text").GetString() };

        var propertyName = type switch
        {
            "image_url" => "image_url",
            "input_audio" => "input_audio",
            "input_video" => "input_video",
            "input_file" => "input_file",
            _ => null
        };
        if (propertyName is null || !part.TryGetProperty(propertyName, out var media))
            throw new ArgumentException($"Unsupported Google embedding content part type '{type}'.");

        var value = ReadGoogleMediaValue(media);
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return new JsonObject { ["fileData"] = new JsonObject { ["fileUri"] = value } };

        var comma = value.IndexOf(',');
        var semicolon = value.IndexOf(';');
        if (comma <= 5 || semicolon <= 5 || semicolon > comma)
            throw new ArgumentException("Google inline media must be a valid base64 data URI.");
        return new JsonObject
        {
            ["inlineData"] = new JsonObject
            {
                ["mimeType"] = value[5..semicolon],
                ["data"] = value[(comma + 1)..]
            }
        };
    }

    private static string ReadGoogleMediaValue(JsonElement media)
    {
        if (media.ValueKind == JsonValueKind.String)
            return media.GetString()!;
        if (media.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "url", "data" })
                if (media.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString()!;
        }
        throw new ArgumentException("Google media requires a URI or data URI.");
    }

    private OpenAIEmbeddingResponse ToGoogleOpenAIResponse(JsonElement root, string requestedModel)
    {
        var embeddings = new List<JsonElement>();
        if (root.TryGetProperty("embeddings", out var batch) && batch.ValueKind == JsonValueKind.Array)
            embeddings.AddRange(batch.EnumerateArray().Select(ReadGoogleValues));
        else if (root.TryGetProperty("embedding", out var single))
            embeddings.Add(ReadGoogleValues(single));

        var tokens = 0;
        if (root.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("billableCharacterCount", out var billable)
            && billable.TryGetInt32(out var count))
            tokens = count;

        return new OpenAIEmbeddingResponse
        {
            Model = requestedModel.ToModelId(GetIdentifier()),
            Data = embeddings.Select((embedding, index) => new OpenAIEmbeddingData { Index = index, Embedding = embedding }).ToArray(),
            Usage = new OpenAIEmbeddingUsage { PromptTokens = tokens, TotalTokens = tokens }
        };
    }

    private static JsonElement ReadGoogleValues(JsonElement embedding)
    {
        if (!embedding.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Google embedding response did not contain a values array.");
        return values.Clone();
    }

    private sealed record GoogleEmbeddingResult(JsonElement Root, IDictionary<string, string> Headers);
}
