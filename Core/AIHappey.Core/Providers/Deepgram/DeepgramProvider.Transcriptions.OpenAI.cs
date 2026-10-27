using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    private void ValidateDeepgramOpenAI(OpenAITranscriptionRequest options)
    {
        options.ValidateOpenAITranscriptionRequest(GetIdentifier());
        if (options.Prompt != null || options.Temperature != null)
            throw new NotSupportedException("Deepgram does not support OpenAI prompt or temperature.");
        if (options.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("Deepgram OpenAI transcription accepts standard fields only, not additional provider options.");
        if (options.TimestampGranularities?.Any(x => x is not "word" and not "segment") == true)
            throw new ArgumentException("timestamp_granularities must contain word or segment.");
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ValidateDeepgramOpenAI(options);
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var values = request.ProviderOptions?.GetValueOrDefault(GetIdentifier()).ValueKind == JsonValueKind.Object
            ? request.ProviderOptions[GetIdentifier()].EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
            : new Dictionary<string, JsonElement>();
        // Verbose and subtitle formats require segments even when words are requested too.
        if (format is "verbose_json" or "diarized_json" or "srt" or "vtt")
            values["utterances"] = JsonSerializer.SerializeToElement(true);
        if (format == "diarized_json") values["diarize_model"] = JsonSerializer.SerializeToElement("latest");
        request.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(values) };
        var response = await TranscriptionRequest(request, cancellationToken);
        var normalized = NormalizeListenResponse(response.ProviderMetadata![GetIdentifier()]);
        var usage = normalized.Duration is { } seconds ? new OpenAITranscriptionDurationUsage { Seconds = seconds } : null;
        return format switch
        {
            "verbose_json" => new OpenAITranscriptionVerboseResponse
            {
                Text = normalized.Text, Language = normalized.Language ?? options.Language ?? "", Duration = normalized.Duration ?? 0,
                Usage = usage,
                Words = normalized.Words.Select(w => new OpenAITranscriptionWord { Word = w.Text, Start = w.Start, End = w.End }).ToArray(),
                Segments = normalized.Segments.Select((s, i) => new OpenAITranscriptionSegment
                {
                    Id = i, Seek = 0, Start = s.Start, End = s.End, Text = s.Text, Tokens = [],
                    Temperature = 0, AverageLogprob = 0, CompressionRatio = 0, NoSpeechProbability = 0
                }).ToArray()
            },
            "diarized_json" => new OpenAITranscriptionDiarizedResponse
            {
                Text = normalized.Text, Duration = normalized.Duration ?? 0, Usage = usage,
                Segments = normalized.Segments.Select((s, i) => new OpenAITranscriptionTextSegment
                {
                    Id = i.ToString(CultureInfo.InvariantCulture), Start = s.Start, End = s.End, Text = s.Text,
                    Speaker = s.Speaker is null ? $"channel_{s.Channel}" : $"channel_{s.Channel}_speaker_{s.Speaker}"
                }).ToArray()
            },
            "srt" or "vtt" => new OpenAITranscriptionResponse { Text = FormatListenSubtitles(normalized.Segments, format == "vtt"), Usage = usage },
            _ => new OpenAITranscriptionResponse { Text = normalized.Text, Usage = usage }
        };
    }

    private static string FormatListenSubtitles(IEnumerable<ListenSpan> spans, bool vtt)
    {
        var builder = new StringBuilder(vtt ? "WEBVTT\n\n" : "");
        var index = 0;
        foreach (var span in spans)
        {
            builder.Append(++index).Append('\n').Append(Time(span.Start)).Append(" --> ").Append(Time(span.End))
                .Append('\n').Append(span.Text.Replace("\r", "").Replace("\n", " ")).Append("\n\n");
        }
        return builder.ToString();
        string Time(double seconds)
        {
            var milliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
            return string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}{(vtt ? '.' : ',')}{milliseconds % 1000:000}");
        }
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateDeepgramOpenAI(options);
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        if (format is not "json" and not "text")
            throw new NotSupportedException("Deepgram OpenAI streaming supports json/text only.");
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var text = new StringBuilder();
        await foreach (var part in TranscriptionStreamingAsync(new StreamingTranscriptionRequest
        {
            Model = request.Model, Audio = request.Audio.ToString()!,
            InputAudioFormat = new() { Type = request.MediaType }, ProviderOptions = request.ProviderOptions
        }, cancellationToken))
        {
            if (part is TranscriptionFinalPart final && !string.IsNullOrWhiteSpace(final.Text))
            {
                var delta = (text.Length == 0 ? "" : " ") + final.Text;
                text.Append(delta);
                yield return new OpenAITranscriptionTextDelta { Delta = delta };
            }
            else if (part is TranscriptionErrorPart error)
            {
                var nonfatal = error.Error is JsonElement raw && (ListenString(raw, "type") == "Warning"
                    || raw.TryGetProperty("fatal", out var fatal) && fatal.ValueKind == JsonValueKind.False);
                if (!nonfatal) throw new InvalidOperationException($"Deepgram transcription error: {error.Error}");
            }
            else if (part is TranscriptionFinishPart)
                yield return new OpenAITranscriptionTextDone { Text = text.ToString() };
        }
    }
}
