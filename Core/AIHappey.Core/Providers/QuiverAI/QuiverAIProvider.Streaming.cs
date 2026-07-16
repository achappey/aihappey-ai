using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    private async Task<ResponseResult> ResponsesCoreAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        return await this.GetResponse(_client, options, cancellationToken: cancellationToken);
    }

    private IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingCoreAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        return this.GetResponses(_client, options, cancellationToken: cancellationToken);
    }
}

