using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.SmallestAI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

