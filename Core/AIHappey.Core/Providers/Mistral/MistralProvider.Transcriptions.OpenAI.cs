using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{


    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);

        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.ValidateOpenAITranscriptionRequest();

        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var native = new Dictionary<string, JsonElement>(options.AdditionalProperties ?? [], StringComparer.Ordinal);
        foreach (var option in AudioProviderOptions(request.ProviderOptions))
            native[option.Key] = option.Value;
        using var form = CreateStreamingTranscriptionForm(bytes, request.MediaType, NormalizeAudioModel(request.Model), native, new List<object>());
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        using var response = await SendAudioStreamingAsync(message, cancellationToken);
        var segmentIndex = 0;
        await foreach (var item in ReadAudioEventsAsync(response, "transcription.done", cancellationToken))
        {
            var payload = item.Payload;
            if (item.Type == "transcription.text.delta")
                yield return new OpenAITranscriptionTextDelta { Delta = RequiredAudioString(payload, "text") };
            else if (item.Type == "transcription.segment.delta")
            {
                var start = AudioNumber(payload, "start");
                var end = AudioNumber(payload, "end");
                var speaker = AudioString(payload, "speaker_id");
                // The OpenAI diarized segment contract requires all three.
                if (start.HasValue && end.HasValue && speaker is not null)
                    yield return new OpenAITranscriptionTextSegment
                    {
                        Id = segmentIndex.ToString(CultureInfo.InvariantCulture),
                        Text = RequiredAudioString(payload, "text"),
                        Start = start.Value,
                        End = end.Value,
                        Speaker = speaker
                    };
                segmentIndex++;
            }
            else if (item.Type == "transcription.done")
                yield return new OpenAITranscriptionTextDone
                {
                    Text = RequiredAudioString(payload, "text"),
                    Usage = payload.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null ? usage.Clone() : null
                };
        }
    }
}
