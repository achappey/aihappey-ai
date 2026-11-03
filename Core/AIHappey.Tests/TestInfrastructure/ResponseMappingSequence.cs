using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.TestInfrastructure;

internal static class ResponseMappingSequence
{
    // Each fixture enumeration represents an independent stream, even on the same async context.
    internal static IEnumerable<AIStreamEvent> MapUnifiedResponseStream(
        this IEnumerable<ResponseStreamPart> parts, string providerId)
    {
        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        foreach (var part in parts)
        foreach (var streamEvent in part.ToUnifiedStreamEvent(providerId, state))
            yield return streamEvent;
    }
}
