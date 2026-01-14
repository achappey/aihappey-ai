using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Responses;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AsyncAI;

public partial class AsyncAIProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // asyncAI does not expose a public list-models endpoint (as of docs provided).
        // We expose the documented model ids for discovery.
        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>(
        [
            new()
            {
                OwnedBy = "asyncAI",
                Type = "speech",
                Name = "AsyncFlow V2 (English)",
                Id = "asyncflow_v2.0".ToModelId(GetIdentifier())
            },
            new()
            {
                OwnedBy = "asyncAI",
                Type = "speech",
                Name = "AsyncFlow Multilingual V1",
                Id = "asyncflow_multilingual_v1.0".ToModelId(GetIdentifier())
            }
        ]);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

