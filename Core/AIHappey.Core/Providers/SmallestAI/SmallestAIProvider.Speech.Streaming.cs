using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one speech text part is required.", nameof(request));

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        TtsSynthesisSettings? settings = null;
        var text = new StringBuilder();
        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Speech parts require type 'text' and non-null text.", nameof(request));
            var options = ReadStreamingSpeechOptions(request.ProviderOptions);
            foreach (var option in ReadStreamingSpeechOptions(part.ProviderOptions))
                options[option.Key] = option.Value;
            if (ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate") is { } rate)
                options["sampleRate"] = JsonSerializer.SerializeToElement(rate);
            var synthesis = new SpeechRequest
            {
                Model = request.Model,
                Text = "validation", // Parts may be empty or end mid-word; validate the complete script below.
                Voice = part.Voice ?? request.Voice,
                Speed = request.Speed,
                Language = request.Language,
                OutputFormat = request.OutputFormat is null ? null : NormalizeStreamingTtsFormat(request.OutputFormat.Type),
                ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(options) }
            };
            var effective = PrepareTtsSynthesis(synthesis, warnings);
            if (settings is not null && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(settings), JsonSerializer.SerializeToElement(effective)))
                throw new NotSupportedException("SmallestAI SSE speech requires one effective voice and synthesis configuration across all text parts.");
            settings ??= effective;
            text.Append(part.Text);
        }
        if (string.IsNullOrWhiteSpace(text.ToString()) || text.ToString().Trim().Length > 8000)
            throw new ArgumentException("SmallestAI speech text must contain between 1 and 8000 characters after trimming.", nameof(request));

        ApplyAuthHeader();
        using var message = CreateTtsStreamMessage(text.ToString(), settings!);
        using var response = await SendTtsStreamAsync(message, cancellationToken);
        yield return new SpeechStreamStartPart { Warnings = warnings };
        yield return new SpeechResponseMetadataPart
        {
            ModelId = request.Model.ToModelId(GetIdentifier()),
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders()
        };
        await foreach (var item in ReadTtsSseAsync(response, cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = item.Payload };
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Audio.Length > 0)
                yield return new SpeechAudioPart
                {
                    Audio = Convert.ToBase64String(item.Audio),
                    AudioFormat = new AudioFormat
                    {
                        Type = settings!.OutputFormat == "pcm" ? "audio/pcm" : ResolveMimeType(settings.OutputFormat, settings.SampleRate),
                        Rate = settings.SampleRate
                    }
                };
            if (item.Done)
                yield return new SpeechFinishPart();
        }
    }

    private static Dictionary<string, JsonElement> ReadStreamingSpeechOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue(ProviderId, out var native) != true) return new();
        if (native.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SmallestAI speech provider options must be an object.");
        return native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
    }

    private static string NormalizeStreamingTtsFormat(string format) => NormalizeOutputFormat(format?.Trim().ToLowerInvariant() switch
    {
        "audio/pcm" or "audio/l16" => "pcm",
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/basic" or "audio/ulaw" => "ulaw",
        "audio/alaw" => "alaw",
        null or "" => throw new ArgumentException("Speech output format type is required."),
        var value => value
    });
}
