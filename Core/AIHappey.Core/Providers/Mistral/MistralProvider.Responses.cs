using OAIC = OpenAI.Chat;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.AI;
using System.Text.Json;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

}