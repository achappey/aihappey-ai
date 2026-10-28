using System.Buffers.Binary;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TwelveLabs;

public sealed partial class TwelveLabsProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await SendEmbeddingAsync(request, cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendEmbeddingAsync(request.ToOpenAIEmbeddingRequest(GetIdentifier()), cancellationToken);
        return new EmbeddingResponse
        {
            Embeddings = result.Response.Data.Select(item => item.Embedding.Deserialize<float[]>()!),
            Usage = result.Response.Usage is { } usage ? new EmbeddingUsage { Tokens = usage.PromptTokens } : null,
            Response = new EmbeddingResponseMetadata { Headers = result.Headers, Body = result.Root },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Warnings = []
        };
    }

    private async Task<EmbeddingResult> SendEmbeddingAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        if (request.User is not null)
            throw new NotSupportedException("TwelveLabs text embeddings do not support the user field.");

        string[] texts;
        if (request.Input.ValueKind == JsonValueKind.String)
            texts = [request.Input.GetString()!];
        else if (request.Input.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            texts = request.Input.EnumerateArray().Select(item => item.GetString()!).ToArray();
        else
            throw new NotSupportedException("TwelveLabs text embeddings require strings, not token IDs.");

        var model = request.Model.StartsWith("twelvelabs/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["twelvelabs/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));
        var inputType = model switch
        {
            "marengo3.0" => "text",
            "marengo3.5" => "multi_input",
            _ => throw new NotSupportedException($"TwelveLabs text embeddings do not support model '{model}'.")
        };
        var dimensions = request.Dimensions;
        if (dimensions is null
            && request.AdditionalProperties?.TryGetValue("embedding_dimension", out var nativeDimensions) == true)
        {
            if (nativeDimensions.ValueKind != JsonValueKind.Number || !nativeDimensions.TryGetInt32(out var value))
                throw new ArgumentException("embedding_dimension must be an integer.", nameof(request));
            dimensions = value;
        }
        if (dimensions is not null)
        {
            if (model == "marengo3.0")
                throw new NotSupportedException("Marengo 3.0 does not support embedding dimensions.");
            if (dimensions is not (128 or 256 or 512))
                throw new ArgumentException("Marengo 3.5 embedding dimensions must be 128, 256, or 512.", nameof(request));
        }
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No TwelveLabs API key.");

        var data = new List<OpenAIEmbeddingData>();
        var results = new List<JsonElement>();
        IDictionary<string, string> headers = new Dictionary<string, string>();
        var tokens = 0;
        var completeUsage = true;
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in request.AdditionalProperties ?? [])
                payload[property.Key] = property.Value.Clone();
            payload["model_name"] = JsonSerializer.SerializeToElement(model);
            payload["input_type"] = JsonSerializer.SerializeToElement(inputType);
            var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (payload.TryGetValue(inputType, out var nativeInput))
            {
                if (nativeInput.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException($"{inputType} must be an object.", nameof(request));
                foreach (var property in nativeInput.EnumerateObject())
                    input[property.Name] = property.Value.Clone();
            }
            input["input_text"] = JsonSerializer.SerializeToElement(text);
            payload[inputType] = JsonSerializer.SerializeToElement(input);
            if (request.Dimensions is not null)
                payload["embedding_dimension"] = JsonSerializer.SerializeToElement(request.Dimensions.Value);

            using var message = new HttpRequestMessage(HttpMethod.Post, "embed-v2")
            {
                Content = JsonContent.Create(payload)
            };
            message.Headers.Add("x-api-key", key);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"TwelveLabs embedding request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                    null, response.StatusCode);

            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("data", out var nativeData)
                    || nativeData.ValueKind != JsonValueKind.Array || nativeData.GetArrayLength() != 1
                    || nativeData[0].ValueKind != JsonValueKind.Object
                    || !nativeData[0].TryGetProperty("embedding", out var embedding)
                    || embedding.ValueKind != JsonValueKind.Array || embedding.GetArrayLength() == 0)
                    throw new JsonException("Expected one non-empty embedding vector per input text.");
                var values = embedding.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var value) && float.IsFinite(value)
                        ? value : throw new JsonException("Expected finite float embedding values.")).ToArray();
                data.Add(new OpenAIEmbeddingData
                {
                    Index = data.Count,
                    Embedding = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
                        ? JsonSerializer.SerializeToElement(EncodeBase64(values)) : embedding.Clone()
                });

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
                {
                    if (usage.ValueKind != JsonValueKind.Object
                        || !usage.TryGetProperty("input_tokens", out var inputTokens)
                        || inputTokens.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Expected modality input token counts.");
                    foreach (var modality in inputTokens.EnumerateObject())
                    {
                        if (modality.Value.ValueKind != JsonValueKind.Number
                            || !modality.Value.TryGetInt32(out var count) || count < 0
                            || count > int.MaxValue - tokens)
                            throw new JsonException("Expected non-negative input token counts within range.");
                        tokens += count;
                    }
                }
                else
                    completeUsage = false;
                results.Add(root.Clone());
                headers = response.GetHeaders();
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("TwelveLabs embedding request returned an invalid response.", exception);
            }
        }

        return new EmbeddingResult(new OpenAIEmbeddingResponse
        {
            Model = $"{GetIdentifier()}/{model}",
            Data = data,
            // Marengo 3.0 does not report token usage.
            Usage = completeUsage ? new OpenAIEmbeddingUsage { PromptTokens = tokens, TotalTokens = tokens } : null!
        }, JsonSerializer.SerializeToElement(new { results }), headers);
    }

    private static string EncodeBase64(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        return Convert.ToBase64String(bytes);
    }

    private sealed record EmbeddingResult(
        OpenAIEmbeddingResponse Response, JsonElement Root, IDictionary<string, string> Headers);
}
