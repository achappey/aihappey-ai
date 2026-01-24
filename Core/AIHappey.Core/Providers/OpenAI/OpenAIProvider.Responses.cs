using System.Net.Http.Headers;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        return _client.GetResponsesUpdates(
           options, ct: cancellationToken);
    }
}
