using AIHappey.Core.AI;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
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