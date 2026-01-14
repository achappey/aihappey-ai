using AIHappey.Core.AI;
using AIHappey.Common.Model.Responses;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (!_client.DefaultRequestHeaders.Contains("Authorization"))
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (!_client.DefaultRequestHeaders.Contains("Authorization"))
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        return _client.GetResponsesUpdates(
           options, ct: cancellationToken);
    }
}
