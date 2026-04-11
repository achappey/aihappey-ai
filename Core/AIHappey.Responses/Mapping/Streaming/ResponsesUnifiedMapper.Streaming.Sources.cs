using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static AIEventEnvelope CreateSourceUrlEnvelope(string id, string url,
        string title, string type,
        string? containerId = null,
        string? fileId = null,
        string? filename = null,
        Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
    => new()
    {
        Type = "source-url",
        Id = id,
        Data = new AISourceUrlEventData
        {
            SourceId = url,
            Url = url,
            Title = title,
            Type = type,
            Filename = filename,
            ContainerId = containerId,
            FileId = fileId,
            ProviderMetadata = providerMetadata
        },
    };

    private static IEnumerable<AIEventEnvelope> CreateSourceUrlEnvelopesFromSearchResults(
        string providerId,
        JsonElement? searchResults,
        string sourceType,
        string idPrefix)
    {
        if (searchResults is not JsonElement results || results.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;

        foreach (var result in results.EnumerateArray())
        {
            index++;

            if (!TryBuildSearchResultSourceEnvelope(
                    providerId,
                    result,
                    sourceType,
                    out var url,
                    out var title,
                    out var providerMetadata))
            {
                continue;
            }

            yield return CreateSourceUrlEnvelope(
                $"{idPrefix}:{index}",
                url!,
                title ?? url!,
                sourceType,
                providerMetadata: providerMetadata);
        }
    }

    private static bool TryBuildSearchResultSourceEnvelope(
        string providerId,
        JsonElement source,
        string sourceType,
        out string? url,
        out string? title,
        out Dictionary<string, Dictionary<string, object>>? providerMetadata)
    {
        url = null;
        title = null;
        providerMetadata = null;

        string? date = null;
        string? lastUpdated = null;
        string? snippet = null;
        string? origin = null;

        if (source.ValueKind == JsonValueKind.Object)
        {
            url = ExtractValue<string>(source, "url")
                ?? ExtractValue<string>(source, "origin_url")
                ?? ExtractValue<string>(source, "image_url");
            title = ExtractValue<string>(source, "title");
            date = ExtractValue<string>(source, "date");
            lastUpdated = ExtractValue<string>(source, "last_updated");
            snippet = ExtractValue<string>(source, "snippet");
            origin = ExtractValue<string>(source, "source");
        }
        else if (source.ValueKind == JsonValueKind.String)
        {
            url = source.GetString();
        }

        if (string.IsNullOrWhiteSpace(url))
            return false;

        providerMetadata = new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = new Dictionary<string, object>
            {
                ["source_type"] = sourceType,
                ["raw"] = source.Clone()
            }
        };

        if (!string.IsNullOrWhiteSpace(origin))
            providerMetadata[providerId]["origin"] = origin;

        if (!string.IsNullOrWhiteSpace(snippet))
            providerMetadata[providerId]["snippet"] = snippet;

        if (!string.IsNullOrWhiteSpace(date))
            providerMetadata[providerId]["date"] = date;

        if (!string.IsNullOrWhiteSpace(lastUpdated))
            providerMetadata[providerId]["last_updated"] = lastUpdated;

        return true;
    }
}
