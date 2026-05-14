using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedWithVeniceCitationsAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var seenSourceUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var streamEvent in this.StreamUnifiedViaChatCompletionsAsync(
                           request,
                           rawChunkMapper: update => CreateVeniceCitationEvents(update, seenSourceUrls),
                           cancellationToken: cancellationToken))
        {
            TrackSeenSourceUrl(streamEvent, seenSourceUrls);
            yield return streamEvent;
        }
    }

    private IEnumerable<AIStreamEvent> CreateVeniceCitationEvents(
        ChatCompletionUpdate update,
        HashSet<string> seenSourceUrls)
    {
        if (update.AdditionalProperties?.TryGetValue("venice_parameters", out var veniceParameters) != true
            || veniceParameters.ValueKind != JsonValueKind.Object
            || !TryGetProperty(veniceParameters, "web_search_citations", out var citations)
            || citations.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var rawStreamEvent = update.ToUnifiedStreamEvent(GetIdentifier());
        var timestamp = rawStreamEvent.Event.Timestamp ?? DateTimeOffset.UtcNow;
        var eventId = rawStreamEvent.Event.Id ?? update.Id;
        var citationIndex = 0;

        foreach (var citation in citations.EnumerateArray())
        {
            citationIndex++;

            if (citation.ValueKind != JsonValueKind.Object)
                continue;

            var url = TryGetString(citation, "url");
            if (string.IsNullOrWhiteSpace(url) || !seenSourceUrls.Add(url))
                continue;

            var title = TryGetString(citation, "title") ?? url;

            yield return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Event = new AIEventEnvelope
                {
                    Type = "source-url",
                    Id = eventId,
                    Timestamp = timestamp,
                    Data = new AISourceUrlEventData
                    {
                        SourceId = url,
                        Url = url,
                        Title = title,
                        Type = "url_citation",
                        ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                        {
                            [GetIdentifier()] = BuildVeniceCitationProviderMetadata(citation, citationIndex)
                        }
                    }
                },
                Metadata = rawStreamEvent.Metadata
            };
        }
    }

    private static Dictionary<string, object> BuildVeniceCitationProviderMetadata(JsonElement citation, int citationIndex)
    {
        var metadata = new Dictionary<string, object>
        {
            ["citation_index"] = citationIndex,
            ["citation_type"] = "web_search_citation",
            ["raw"] = citation.Clone()
        };

        AddIfNotWhiteSpace(metadata, "url", citation.TryGetString("url"));
        AddIfNotWhiteSpace(metadata, "title", citation.TryGetString("title"));
        AddIfNotWhiteSpace(metadata, "date", citation.TryGetString("date"));
        AddIfNotWhiteSpace(metadata, "content", citation.TryGetString("content"));

        return metadata;
    }

    private static void TrackSeenSourceUrl(AIStreamEvent streamEvent, HashSet<string> seenSourceUrls)
    {
        if (streamEvent.Event.Data is AISourceUrlEventData sourceEvent
            && !string.IsNullOrWhiteSpace(sourceEvent.Url))
        {
            seenSourceUrls.Add(sourceEvent.Url);
        }
    }

    private static void AddIfNotWhiteSpace(Dictionary<string, object> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
