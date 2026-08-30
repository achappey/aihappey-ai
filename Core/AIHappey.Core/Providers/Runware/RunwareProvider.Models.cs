using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private const int ModelSearchLimit = 100;

    private static readonly ModelSearchDefinition[] ModelSearches =
    [
        new("tts", "audio", "audio", "speech"),
        new("video", "video", "video", "video"),
        new("ai", "text", "text", "language"),
        new("image", null, "image", "image")
    ];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var requests = ModelSearches
                    .Select(search => new ModelSearchRequest
                    {
                        TaskUUID = Guid.NewGuid(),
                        Search = search.Search,
                        Category = search.Category
                    })
                    .ToArray();

                using var response = await _client.PostAsJsonAsync(string.Empty, requests, JsonSerializerOptions.Web, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"Runware model-search error ({(int)response.StatusCode}): {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var envelope = await JsonSerializer.DeserializeAsync<ModelSearchEnvelope>(
                    stream,
                    JsonSerializerOptions.Web,
                    cancellationToken);

                if (envelope?.Data is null)
                    throw new InvalidOperationException("Runware returned an invalid model-search response.");

                var responsesByTask = envelope.Data
                    .Where(item => item.TaskType.Equals("modelSearch", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(item => item.TaskUUID)
                    .ToDictionary(group => group.Key, group => group.First());

                var models = new List<Model>();
                var seenAirIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var index = 0; index < requests.Length; index++)
                {
                    var request = requests[index];
                    var search = ModelSearches[index];

                    if (!responsesByTask.TryGetValue(request.TaskUUID, out var result))
                        throw new InvalidOperationException(
                            $"Runware model-search response did not include task '{request.TaskUUID}'.");

                    foreach (var item in result.Results ?? [])
                    {
                        if (!ShouldExpose(item, search.OutputType)
                            || !seenAirIdentifiers.Add(item.Air!))
                            continue;

                        models.Add(MapModel(item, search.ModelType));
                    }
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }

    private Model MapModel(ModelSearchResult item, string modelType)
    {
        var tags = (item.Tags ?? [])
            .Concat(item.Capabilities ?? [])
            .Concat(string.IsNullOrWhiteSpace(item.Category) ? [] : [$"category:{item.Category}"])
            .Concat(string.IsNullOrWhiteSpace(item.Source) ? [] : [$"source:{item.Source}"])
            .Concat(string.IsNullOrWhiteSpace(item.Architecture) ? [] : [$"architecture:{item.Architecture}"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new Model
        {
            Id = item.Air!.ToModelId(GetIdentifier()),
            Name = string.IsNullOrWhiteSpace(item.Name) ? item.Air : item.Name,
            Description = item.Comment,
            OwnedBy = string.IsNullOrWhiteSpace(item.Creator?.Name)
                ? item.Air.Split(':', 2)[0]
                : item.Creator.Name,
            Type = modelType,
            Created = item.UpdatedDateUnixTimestamp > 0
                ? item.UpdatedDateUnixTimestamp
                : item.AddedUnixTimestamp > 0
                    ? item.AddedUnixTimestamp
                    : null,
            Tags = tags.Length == 0 ? null : tags
        };
    }

    private static bool ShouldExpose(ModelSearchResult item, string outputType)
        => item.Private is false
            && !string.IsNullOrWhiteSpace(item.Air)
            && item.Capabilities?.Any(capability =>
                capability.StartsWith("io:", StringComparison.OrdinalIgnoreCase)
                && capability.EndsWith($"-to-{outputType}", StringComparison.OrdinalIgnoreCase)) is true;

    private sealed record ModelSearchDefinition(
        string Search,
        string? Category,
        string OutputType,
        string ModelType);

    private sealed class ModelSearchRequest
    {
        public string TaskType { get; init; } = "modelSearch";

        public Guid TaskUUID { get; init; }

        public required string Search { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Category { get; init; }

        public int Limit { get; init; } = ModelSearchLimit;
    }

    private sealed class ModelSearchEnvelope
    {
        public ModelSearchResponse[]? Data { get; init; }
    }

    private sealed class ModelSearchResponse
    {
        public Guid TaskUUID { get; init; }

        public string TaskType { get; init; } = string.Empty;

        public ModelSearchResult[]? Results { get; init; }
    }

    private sealed class ModelSearchResult
    {
        public string? Name { get; init; }

        public string? Air { get; init; }

        public string[]? Tags { get; init; }

        public string? Category { get; init; }

        public bool Private { get; init; }

        public string? Comment { get; init; }

        public string? Architecture { get; init; }

        public string[]? Capabilities { get; init; }

        public long AddedUnixTimestamp { get; init; }

        public long UpdatedDateUnixTimestamp { get; init; }

        public string? Source { get; init; }

        public ModelSearchCreator? Creator { get; init; }
    }

    private sealed class ModelSearchCreator
    {
        public string? Name { get; init; }
    }
}
