using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Lightcone;

public partial class LightconeProvider
{
    private async Task<ResponseResult> ExecuteLightconeResponseAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        NormalizeResponseModel(options);
        var providerHeaders = this.SetDefaultResponseProperties(options);
        var headers = CreateAuthenticatedHeaders(providerHeaders, options.Headers);
        options.Headers = null;

        return await _client.GetResponses(
            options,
            GetIdentifier(),
            relativeUrl: "v1/responses",
            headers: headers,
            ct: cancellationToken);
    }

    private async IAsyncEnumerable<ResponseStreamPart> StreamLightconeResponseAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        NormalizeResponseModel(options);
        var providerHeaders = this.SetDefaultResponseProperties(options);
        var headers = CreateAuthenticatedHeaders(providerHeaders, options.Headers);
        options.Headers = null;

        await foreach (var update in _client.GetResponsesUpdates(
            options,
            providerId: GetIdentifier(),
            relativeUrl: "v1/responses",
            headers: headers,
            ct: cancellationToken))
        {
            yield return update;
        }
    }

    private void NormalizeResponseModel(ResponseRequest options)
    {
        var prefix = $"{GetIdentifier()}/";
        if (options.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            options.Model = options.Model[prefix.Length..];
    }
}
