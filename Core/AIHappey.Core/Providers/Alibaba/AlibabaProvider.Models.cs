using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private const int ModelCatalogPageSize = 200;

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            this.GetCacheKey(),
            FetchModelsAsync,
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> FetchModelsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var models = new List<Model>();
        var pageNumber = 1;

        while (true)
        {
            var path = $"api/v1/models?language=en-US&page_no={pageNumber}&page_size={ModelCatalogPageSize}";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Alibaba Model Studio model-listing error ({(int)response.StatusCode}): {error}",
                    null,
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var root = document.RootElement;
            if (root.TryGetProperty("success", out var successElement)
                && successElement.ValueKind == JsonValueKind.False)
            {
                var message = ReadString(root, "message") ?? "Unknown Alibaba Model Studio error.";
                throw new InvalidOperationException($"Alibaba Model Studio model-listing error: {message}");
            }

            if (!root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Alibaba Model Studio returned an invalid model-listing response.");

            var returnedCount = 0;
            if (output.TryGetProperty("models", out var modelElements)
                && modelElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var modelElement in modelElements.EnumerateArray())
                {
                    var model = ParseModel(modelElement);
                    if (model is null)
                        continue;

                    models.Add(model);
                    returnedCount++;
                }
            }

            var total = ReadInt32(output, "total") ?? models.Count;
            var responsePageNumber = ReadInt32(output, "page_no") ?? pageNumber;
            var responsePageSize = ReadInt32(output, "page_size") ?? ModelCatalogPageSize;

            if (returnedCount == 0
                || models.Count >= total
                || responsePageNumber * responsePageSize >= total)
                break;

            pageNumber = responsePageNumber + 1;
        }

        return models;
    }

    private Model? ParseModel(JsonElement element)
    {
        var modelId = ReadString(element, "model");
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var capabilities = ReadStringArray(element, "capabilities");
        var features = ReadStringArray(element, "features");
        var inferenceProvider = ReadString(element, "inference_provider");
        var provider = ReadString(element, "provider");

        int? contextWindow = null;
        int? maxOutputTokens = null;
        if (element.TryGetProperty("model_info", out var modelInfo)
            && modelInfo.ValueKind == JsonValueKind.Object)
        {
            contextWindow = ReadInt32(modelInfo, "context_window");
            maxOutputTokens = ReadInt32(modelInfo, "max_output_tokens")
                ?? ReadInt32(modelInfo, "reasoning_max_output_tokens");
        }

        var tags = capabilities
            .Concat(features)
            .Concat(string.IsNullOrWhiteSpace(inferenceProvider) ? [] : [inferenceProvider])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Model
        {
            Id = modelId.ToModelId(GetIdentifier()),
            Name = ReadString(element, "name") ?? modelId,
            Description = ReadString(element, "description"),
            OwnedBy = string.IsNullOrWhiteSpace(provider) ? nameof(Alibaba) : provider,
            Type = GetModelType(modelId, capabilities),
            ContextWindow = contextWindow,
            MaxTokens = maxOutputTokens,
            Created = ParsePublishedTime(ReadString(element, "published_time")),
            Tags = tags.Length == 0 ? null : tags
        };
    }

    private static string GetModelType(string modelId, IReadOnlyCollection<string> capabilities)
    {
        if (ContainsAny(capabilities, "IG"))
            return "image";

        if (ContainsAny(capabilities, "VG", "3D-generation"))
            return "video";

        if (ContainsAny(capabilities, "ASR", "Realtime-ASR", "Realtime-Audio-Translate"))
            return "transcription";

        if (ContainsAny(capabilities, "TTS", "Realtime-Text-to-Speech", "Realtime-Chatting"))
            return "speech";

        if (ContainsAny(capabilities, "TR", "ME"))
            return "embedding";

        if (ContainsAny(capabilities, "TG", "Reasoning", "VU", "Multimodal-Omni", "Realtime-Omni"))
            return "language";

        return modelId.GuessModelType();
    }

    public static string GetModelTypeForTests(string modelId, params string[] capabilities)
        => GetModelType(modelId, capabilities);

    private static bool ContainsAny(IReadOnlyCollection<string> values, params string[] candidates)
        => candidates.Any(candidate => values.Contains(candidate, StringComparer.OrdinalIgnoreCase));

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
            return [];

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static int? ReadInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
                ? value
                : null;

    private static long? ParsePublishedTime(string? value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var publishedTime))
            return null;

        return publishedTime.ToUnixTimeSeconds();
    }
}
