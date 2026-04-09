using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Extensions;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Perplexity;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var (request, extraRootProperties) = PrepareResponsesRequest(options);

        return await _client.GetResponses(
                   request,
                   relativeUrl: "v1/agent",
                   ct: cancellationToken,
                   extraRootProperties: extraRootProperties);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var (request, extraRootProperties) = PrepareResponsesRequest(options);

        return _client.GetResponsesUpdates(
           request,
           relativeUrl: "v1/agent",
           ct: cancellationToken,
           extraRootProperties: extraRootProperties);
    }

    private JsonElement? BuildResponsesExtraRootProperties(string model, PerplexityProviderMetadata? metadata)
    {
        var dic = new Dictionary<string, object?>
        {
        };

        if (UsesResponsesPreset(model))
        {
            dic.Add("preset", model);
            dic.Add("model", null);
        }

        if (metadata?.Models?.Any() == true)
        {
            dic.Add("models", metadata.Models);
        }
        if (metadata?.MaxSteps.HasValue == true)
        {
            dic.Add("max_steps", metadata?.MaxSteps);
        }

        return JsonSerializer.SerializeToElement(dic, JsonSerializerOptions.Web);
    }



    private (ResponseRequest Request, JsonElement? ExtraRootProperties) PrepareResponsesRequest(ResponseRequest options)
    {
        var usePreset = UsesResponsesPreset(options.Model);
        var request = CloneResponsesRequest(options);

        this.SetDefaultResponseProperties(request);

        if (usePreset)
            request.Model = null;

        return (request, BuildResponsesExtraRootProperties(options.Model!,
            options?.Metadata?.GetProviderMetadata<PerplexityProviderMetadata>(GetIdentifier())));
    }

    private static ResponseRequest CloneResponsesRequest(ResponseRequest options)
        => new()
        {
            Model = options.Model,
            Instructions = options.Instructions,
            Input = options.Input,
            Temperature = options.Temperature,
            TopP = options.TopP,
            Truncation = options.Truncation,
            MaxOutputTokens = options.MaxOutputTokens,
            TopLogprobs = options.TopLogprobs,
            ParallelToolCalls = options.ParallelToolCalls,
            Stream = options.Stream,
            Store = options.Store,
            ServiceTier = options.ServiceTier,
            Text = options.Text,
            Include = options.Include is null ? null : [.. options.Include],
            Metadata = options.Metadata is null ? null : new Dictionary<string, object?>(options.Metadata),
            Tools = options.Tools is null ? null : [.. options.Tools],
            ToolChoice = options.ToolChoice,
            Reasoning = options.Reasoning,
        };
}

