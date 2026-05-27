using AIHappey.Core.AI;
using AIHappey.Responses.Extensions;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Responses.Mapping;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (options.Model?.StartsWith($"sonar") == true)
        {
            var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
                  cancellationToken);

            return result.ToResponseResult();
        }

        ApplyAuthHeader();

        var request = PrepareResponsesRequest(options);

        var response = await this.GetResponse(_client,
                   request,
                   relativeUrl: "v1/agent",
                   cancellationToken: cancellationToken);

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

        if (options.Model?.StartsWith($"sonar") == true)
        {
            var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

            await foreach (var part in this.StreamUnifiedAsync(
                unifiedRequest,
                cancellationToken))
            {
                yield return part.ToResponseStreamPart();
            }

            yield break;
        }

        ApplyAuthHeader();

        var request = PrepareResponsesRequest(options);

        await foreach (var update in this.GetResponses(_client,
                           request,
                           relativeUrl: "v1/agent",
                           cancellationToken: cancellationToken))
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

        var sonarOptions = new List<string>
        {
            "search_mode",
            "reasoning_effort",
            "return_images",
            "disable_search",
            "return_related_questions",
            "search_recency_filter",
            "enable_search_classifier",
            "search_after_date_filter",
            "search_before_date_filter",
            "last_updated_after_filter",
            "last_updated_before_filter",
            "web_search_options",
            "media_response"
        };

        foreach (var opt in sonarOptions)
        {
            if (options.AdditionalProperties?.ContainsKey(opt) == true)
                options.AdditionalProperties.Remove(opt);
        }

        return options;
    }
}

