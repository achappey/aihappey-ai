using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.SmallestAI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

