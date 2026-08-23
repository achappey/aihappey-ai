using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    private static readonly JsonSerializerOptions PerplexityEmbeddingJson = new(JsonSerializerDefaults.Web);

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ValidatePerplexityRequest(request);

        var result = await SendPerplexityEmbeddingAsync(request, cancellationToken);
        return ToPerplexityOpenAIResponse(result.Root, request.Model);
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        RejectBinaryVercelEncoding(request);

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        openAIRequest.EncodingFormat = "base64_int8";

        var result = await SendPerplexityEmbeddingAsync(openAIRequest, cancellationToken);
        var openAIResponse = ToPerplexityOpenAIResponse(result.Root, openAIRequest.Model);
        var decodedResponse = DecodeInt8Embeddings(openAIResponse);

        return new OpenAICompatibleEmbeddingResult(decodedResponse, result.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()));
    }

    private async Task<PerplexityEmbeddingResult> SendPerplexityEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePerplexityRequest(request);

        var contextual = IsContextualEmbeddingModel(request.Model);
        var payload = CopyPerplexityOptions(request.AdditionalProperties);
        payload["model"] = request.Model;
        payload["input"] = contextual ? ToContextualInput(request.Input) : RequirePerplexityTextInput(request.Input);

        if (request.Dimensions is not null)
            payload["dimensions"] = request.Dimensions.Value;

        payload["encoding_format"] = NormalizePerplexityEncoding(request.EncodingFormat);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            contextual ? "v1/contextualizedembeddings" : "v1/embeddings")
        {
            Content = new StringContent(
                payload.ToJsonString(PerplexityEmbeddingJson),
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
                    ? $"Perplexity embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Perplexity embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return new PerplexityEmbeddingResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Perplexity embedding request returned invalid JSON.", exception);
        }
    }

    private static void ValidatePerplexityRequest(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Dimensions is <= 0)
            throw new ArgumentException("Dimensions must be a positive integer.", nameof(request));

        _ = IsContextualEmbeddingModel(request.Model)
            ? ToContextualInput(request.Input)
            : RequirePerplexityTextInput(request.Input);
        _ = NormalizePerplexityEncoding(request.EncodingFormat);
    }

    private static string NormalizePerplexityEncoding(string? encoding)
        => encoding?.ToLowerInvariant() switch
        {
            null or "base64" or "base64_int8" or "float" => "base64_int8",
            "base64_binary" => "base64_binary",
            _ => throw new ArgumentException(
                "Perplexity encoding_format must be 'base64_int8' or 'base64_binary'.",
                nameof(encoding))
        };

    private static bool IsContextualEmbeddingModel(string model)
    {
        var nativeModel = model.Split('/').Last();
        return nativeModel.StartsWith("pplx-embed-context-", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject CopyPerplexityOptions(Dictionary<string, JsonElement>? additional)
    {
        var payload = new JsonObject();
        foreach (var property in additional ?? [])
        {
            if (property.Key.Equals("input", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("encoding_format", StringComparison.OrdinalIgnoreCase)
                || property.Key.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
        }

        return payload;
    }

    private static JsonNode RequirePerplexityTextInput(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(input.GetString()))
            return JsonValue.Create(input.GetString())!;

        if (input.ValueKind == JsonValueKind.Array)
        {
            var values = input.EnumerateArray().ToArray();
            if (values.Length > 0
                && values.All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())))
            {
                return JsonNode.Parse(input.GetRawText())!;
            }
        }

        throw new ArgumentException(
            "Perplexity text embeddings require a non-empty string or array of non-empty strings.",
            nameof(input));
    }

    private static JsonNode ToContextualInput(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(input.GetString()))
            return new JsonArray(new JsonArray(input.GetString()));

        if (input.ValueKind == JsonValueKind.Array)
        {
            var values = input.EnumerateArray().ToArray();
            if (values.Length > 0
                && values.All(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())))
            {
                return new JsonArray(values.Select(item => (JsonNode?)new JsonArray(item.GetString())).ToArray());
            }

            if (values.Length > 0 && values.All(IsNonEmptyStringArray))
                return JsonNode.Parse(input.GetRawText())!;
        }

        throw new ArgumentException(
            "Perplexity contextual embeddings require non-empty text or non-empty nested document/chunk arrays.",
            nameof(input));
    }

    private static bool IsNonEmptyStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return false;

        var chunks = value.EnumerateArray().ToArray();
        return chunks.Length > 0
            && chunks.All(chunk => chunk.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(chunk.GetString()));
    }

    private static OpenAIEmbeddingResponse ToPerplexityOpenAIResponse(JsonElement root, string requestedModel)
    {
        var data = new List<OpenAIEmbeddingData>();
        if (root.TryGetProperty("data", out var outer) && outer.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outer.EnumerateArray())
            {
                if (item.TryGetProperty("embedding", out var embedding))
                {
                    data.Add(new OpenAIEmbeddingData { Index = data.Count, Embedding = embedding.Clone() });
                    continue;
                }

                if (!item.TryGetProperty("data", out var nested) || nested.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var chunk in nested.EnumerateArray())
                {
                    if (chunk.TryGetProperty("embedding", out var chunkEmbedding))
                        data.Add(new OpenAIEmbeddingData { Index = data.Count, Embedding = chunkEmbedding.Clone() });
                }
            }
        }

        var promptTokens = 0;
        var totalTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var parsedPrompt))
                promptTokens = parsedPrompt;
            if (usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var parsedTotal))
                totalTokens = parsedTotal;
        }

        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()
            : requestedModel;

        return new OpenAIEmbeddingResponse
        {
            Model = (model ?? requestedModel).ToModelId("perplexity"),
            Data = data,
            Usage = new OpenAIEmbeddingUsage { PromptTokens = promptTokens, TotalTokens = totalTokens }
        };
    }

    private static OpenAIEmbeddingResponse DecodeInt8Embeddings(OpenAIEmbeddingResponse response)
    {
        foreach (var item in response.Data)
        {
            if (item.Embedding.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"Perplexity embedding at index {item.Index} was not base64 encoded.");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(item.Embedding.GetString()!);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    $"Perplexity embedding at index {item.Index} contained invalid base64 data.",
                    exception);
            }

            var values = bytes.Select(value => (float)unchecked((sbyte)value)).ToArray();
            item.Embedding = JsonSerializer.SerializeToElement(values, PerplexityEmbeddingJson);
        }

        return response;
    }

    private void RejectBinaryVercelEncoding(EmbeddingRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in options.EnumerateObject())
        {
            if (!property.Name.Equals("encoding_format", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Equals("encodingFormat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString()?.Equals("base64_binary", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new ArgumentException(
                    $"providerOptions.{GetIdentifier()}.encoding_format 'base64_binary' cannot be represented by the Vercel float embedding response contract.",
                    nameof(request));
            }
        }
    }

    private sealed record PerplexityEmbeddingResult(JsonElement Root, IDictionary<string, string> Headers);
}
