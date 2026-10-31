using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var synthesis = PrepareStreamingSpeechRequest(request);
        var warnings = new List<object>();
        if (request.OutputFormat?.Rate is not null)
            warnings.Add(new { type = "unsupported", feature = "outputFormat.rate" });
        var (payload, model, format) = await PrepareSpeechStreamPayloadAsync(synthesis, warnings, cancellationToken);
        using var message = CreateSpeechStreamMessage(payload);
        using var response = await SendAudioStreamingAsync(message, cancellationToken);

        yield return new SpeechStreamStartPart { Warnings = warnings };
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        await foreach (var item in ReadAudioEventsAsync(response, "speech.audio.done", cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = item.Payload };
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type == "speech.audio.delta")
            {
                yield return new SpeechAudioPart
                {
                    Audio = RequiredAudioString(item.Payload, "audio_data"),
                    AudioFormat = new AudioFormat { Type = ResolveSpeechMimeType(format) }
                };
            }
            else if (item.Type == "speech.audio.done")
            {
                yield return new SpeechFinishPart
                {
                    Usage = ReadSpeechStreamUsage(item.Payload),
                    ProviderMetadata = AudioMetadata(item.Payload)
                };
            }
        }
    }

    private static SpeechRequest PrepareStreamingSpeechRequest(StreamingSpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one speech text part is required.", nameof(request));
        if (request.OutputFormat is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputFormat.Type);
            if (request.OutputFormat.Rate is <= 0)
                throw new ArgumentException("Speech output sample rate must be positive.", nameof(request));
        }

        var defaults = AudioProviderOptions(request.ProviderOptions);
        Dictionary<string, JsonElement>? selectedOptions = null;
        Dictionary<string, JsonElement>? selectedExtras = null;
        string? selectedVoice = null;
        string? selectedFormat = null;
        var text = new StringBuilder();

        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Speech parts must have type 'text' and non-null text.", nameof(request));

            var options = new Dictionary<string, JsonElement>(defaults, StringComparer.Ordinal);
            foreach (var option in AudioProviderOptions(part.ProviderOptions))
                options[option.Key] = option.Value;
            var native = JsonSerializer.SerializeToElement(options);
            var voice = AudioString(native, "voice_id") ?? part.Voice ?? request.Voice;
            voice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
            var format = NormalizeStreamingSpeechFormat(request.OutputFormat?.Type ?? AudioString(native, "response_format"));
            var extras = options.Where(p => p.Key is not ("input" or "model" or "stream" or "voice_id" or "response_format"))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

            if (selectedOptions is not null &&
                (!string.Equals(selectedVoice, voice, StringComparison.OrdinalIgnoreCase) || selectedFormat != format ||
                 !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(selectedExtras), JsonSerializer.SerializeToElement(extras))))
                throw new NotSupportedException("Mistral speech requires one effective voice and provider-options configuration for all text parts.");

            selectedOptions ??= options;
            selectedExtras ??= extras;
            selectedVoice = voice;
            selectedFormat = format;
            text.Append(part.Text); // Boundaries may be mid-word; never insert separators.
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Speech text is required.", nameof(request));
        return new SpeechRequest
        {
            Model = request.Model,
            Text = text.ToString(),
            Voice = selectedVoice,
            OutputFormat = selectedFormat,
            Instructions = request.Instructions,
            Speed = request.Speed,
            Language = request.Language,
            ProviderOptions = new() { ["mistral"] = JsonSerializer.SerializeToElement(selectedOptions) }
        };
    }

    private async Task<(Dictionary<string, object?> Payload, string Model, string Format)> PrepareSpeechStreamPayloadAsync(
        SpeechRequest request,
        ICollection<object> warnings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = NormalizeAudioModel(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var native = AudioProviderOptions(request.ProviderOptions);
        var metadata = JsonSerializer.SerializeToElement(native).Deserialize<MistralSpeechProviderMetadata>(JsonSerializerOptions.Web);
        var format = NormalizeStreamingSpeechFormat(request.OutputFormat ?? metadata?.ResponseFormat);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        // Selection may query the voice catalog and therefore needs authentication.
        ApplyAuthHeader();
        var selection = await ResolveSpeechSelectionAsync(request, metadata, warnings, cancellationToken);
        var payload = native.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal);
        payload["input"] = request.Text;
        payload["model"] = selection.ModelId;
        payload["stream"] = true;
        payload["response_format"] = format;
        payload.Remove("voice_id");
        if (!string.IsNullOrWhiteSpace(selection.VoiceId))
            payload["voice_id"] = selection.VoiceId;
        return (payload, selection.ModelId, format);
    }

    private static HttpRequestMessage CreateSpeechStreamMessage(Dictionary<string, object?> payload)
        => new(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static string NormalizeStreamingSpeechFormat(string? format)
    {
        var normalized = format?.Trim().ToLowerInvariant() switch
        {
            null or "" => "mp3",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" or "wave" => "wav",
            "audio/flac" => "flac",
            "audio/opus" => "opus",
            "audio/pcm" => "pcm",
            var value => value
        };
        return normalized is "mp3" or "wav" or "flac" or "opus" or "pcm"
            ? normalized
            : throw new NotSupportedException($"Mistral speech output format '{format}' is not supported.");
    }

    private static StreamingSpeechUsage? ReadSpeechStreamUsage(JsonElement payload)
    {
        if (!payload.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
            return null;
        return new StreamingSpeechUsage
        {
            InputCharacters = AudioCount(usage, "input_characters"),
            InputTokens = AudioCount(usage, "input_tokens") ?? AudioCount(usage, "prompt_tokens"),
            OutputTokens = AudioCount(usage, "output_tokens") ?? AudioCount(usage, "completion_tokens"),
            Raw = usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
        };
    }
}
