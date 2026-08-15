using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(options, "v1/audio/transcriptions", cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        return OpenAITranscriptionFallbackStreamingAsync(options, cancellationToken);
    }

    private async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionFallbackStreamingAsync(
        OpenAITranscriptionRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrEmpty(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }
}
