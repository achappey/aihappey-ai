using AIHappey.Core.AI;
using AIHappey.Responses.Extensions;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var request = PrepareResponsesRequest(options);

        var response = await _client.GetResponses(
                   request,
                   relativeUrl: "v1/agent",
                   ct: cancellationToken);

        if (response.Usage is JsonElement usage)
        {
            response.Metadata = ModelCostMetadataEnricher.AddCost(
                response.Metadata,
                TryGetPerplexityTotalCost(usage));
        }

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var request = PrepareResponsesRequest(options);

        await foreach (var update in _client.GetResponsesUpdates(
                           request,
                           relativeUrl: "v1/agent",
                           ct: cancellationToken).WithCancellation(cancellationToken))
        {
            if (update is ResponseCompleted completed
                && completed.Response.Usage is JsonElement usage)
            {
                completed.Response.Metadata = ModelCostMetadataEnricher.AddCost(
                    completed.Response.Metadata,
                    TryGetPerplexityTotalCost(usage));
            }

            yield return update;
        }
    }



    private ResponseRequest PrepareResponsesRequest(ResponseRequest options)
    {
        var model = options.Model;
        var usePreset = UsesResponsesPreset(options.Model);

        this.SetDefaultResponseProperties(options);

        if (usePreset)
        {
            options.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            options.AdditionalProperties["preset"] = JsonSerializer.SerializeToElement(model, JsonSerializerOptions.Web);
            options.Model = null;
        }

        return options;
    }
}

