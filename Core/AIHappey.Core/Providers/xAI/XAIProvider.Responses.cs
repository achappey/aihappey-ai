using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultResponseProperties(options);

        var response = await _client.GetResponses(
                   options, ct: cancellationToken);

        response.Metadata = MergeGatewayCostMetadata(response.Metadata, response.Usage);

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultResponseProperties(options);

        await foreach (var update in _client.GetResponsesUpdates(options, ct: cancellationToken))
        {
            if (update is ResponseCompleted completed)
            {
                completed.Response.Metadata = MergeGatewayCostMetadata(
                    completed.Response.Metadata,
                    completed.Response.Usage);
            }

            yield return update;
        }
    }

    private static Dictionary<string, object?>? MergeGatewayCostMetadata(
        Dictionary<string, object?>? existingMetadata,
        object? usage)
    {
        var gatewayMetadata = CreateGatewayCostMetadata(usage);
        if (gatewayMetadata == null)
            return existingMetadata;

        var merged = existingMetadata != null
            ? new Dictionary<string, object?>(existingMetadata)
            : new Dictionary<string, object?>();

        foreach (var entry in gatewayMetadata)
            merged[entry.Key] = entry.Value;

        return merged;
    }
}
