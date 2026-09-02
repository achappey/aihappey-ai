using AIHappey.Core.AI;
using OpenAI.Audio;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var audioClient = new AudioClient(
            request.Model,
            GetKey()
        );

        var now = DateTime.UtcNow;
        List<string> results = [];
        List<object> warnings = [];
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var memStream = new MemoryStream(bytes, writable: false);
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());
        var options = new AudioTranscriptionOptions();

        if (!string.IsNullOrEmpty(metadata?.Language))
        {
            options.Language = metadata?.Language;
        }

        if (!string.IsNullOrEmpty(metadata?.Prompt))
        {
            options.Prompt = metadata?.Prompt;
        }

        options.Temperature = metadata?.Temperature;

        if (metadata?.TimestampGranularities?.Any() == true)
        {
            options.TimestampGranularities = (metadata.TimestampGranularities.Contains("word")
                                && metadata.TimestampGranularities.Contains("segment"))
                                ? AudioTimestampGranularities.Word | AudioTimestampGranularities.Segment
                                : metadata.TimestampGranularities.Contains("word")
                                    ? AudioTimestampGranularities.Word
                                    : metadata.TimestampGranularities.Contains("segment")
                                        ? AudioTimestampGranularities.Segment
                                        : default;
            options.ResponseFormat = AudioTranscriptionFormat.Verbose;
        }

        var result = await audioClient.TranscribeAudioAsync(memStream,
            "audio" + request.MediaType.GetAudioExtension(),
            options,
            cancellationToken);

        return new TranscriptionResponse()
        {
            Text = result.Value.Text,
            Segments = result.Value.Segments.Select(a => new TranscriptionSegment()
            {
                Text = a.Text,
                StartSecond = (float)a.StartTime.TotalSeconds,
                EndSecond = (float)a.EndTime.TotalSeconds
            }),
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.GetRawResponse().Content.ToString(),
            },
            Language = result.Value.Language,
            DurationInSeconds = result.Value.Duration.HasValue
                ? (float)result.Value.Duration.Value.TotalSeconds : null
        };
    }
    
}
