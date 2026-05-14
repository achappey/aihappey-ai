using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        object? structuredData = null;
        var schemaName = chatRequest.ResponseFormat?.GetJSONSchema()?.JsonSchema?.Name ?? "unknown";

        await foreach (var streamEvent in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            if (TryGetStructuredOutputData(streamEvent, out var nextStructuredData))
            {
                structuredData = nextStructuredData;
                continue;
            }

            if (string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
                && chatRequest.ResponseFormat is not null
                && structuredData is not null)
            {
                yield return new DataUIPart
                {
                    Type = $"data-{schemaName}",
                    Data = structuredData
                };
            }

            foreach (var uiPart in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }
}
