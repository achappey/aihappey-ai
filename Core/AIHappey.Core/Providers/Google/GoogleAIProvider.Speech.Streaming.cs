using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<object>();
        var payload = BuildGoogleStreamingSpeechPayload(request, warnings);
        var model = payload["model"]?.GetValue<string>() ?? request.Model;
        var state = new GoogleSpeechStreamState(payload);
        using var message = CreateGoogleSpeechStreamMessage(payload);
        using var response = await SendGoogleSpeechStreamAsync(message, cancellationToken);

        yield return new SpeechStreamStartPart { Warnings = warnings };
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        await foreach (var item in ReadGoogleSpeechEventsAsync(response, state, cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = item.Payload };
            foreach (var audio in item.Audio)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SpeechAudioPart
                {
                    Id = audio.Id,
                    Audio = audio.Data,
                    AudioFormat = audio.Format,
                    ProviderMetadata = new() { [GetIdentifier()] = audio.Payload }
                };
            }

            if (item.Type == "interaction.completed")
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SpeechFinishPart
                {
                    Usage = state.Usage is { } usage ? new StreamingSpeechUsage
                    {
                        InputCharacters = GoogleSpeechCount(usage, "input_characters", "inputCharacters"),
                        InputTokens = GoogleSpeechInputTokens(usage),
                        OutputTokens = GoogleSpeechOutputTokens(usage),
                        Raw = usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    } : null,
                    ProviderMetadata = BuildGoogleSpeechProviderMetadata(state.MetadataRoot, model)
                };
            }
        }
    }

    private static JsonObject BuildGoogleStreamingSpeechPayload(StreamingSpeechRequest request, ICollection<object> warnings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one speech text part is required.", nameof(request));

        var text = new StringBuilder();
        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Speech parts must have type 'text' and non-null text.", nameof(request));
            text.Append(part.Text); // A boundary may occur mid-word; never add separators.
        }

        var defaults = GoogleSpeechOptions(request.ProviderOptions);
        JsonObject? selected = null;
        foreach (var part in request.Text)
        {
            var native = (JsonObject)defaults.DeepClone();
            foreach (var option in GoogleSpeechOptions(part.ProviderOptions))
                native[option.Key] = option.Value?.DeepClone();
            if (string.IsNullOrWhiteSpace(text.ToString()) && native["input"] is null)
                throw new ArgumentException("Speech text is required.", nameof(request));

            var partWarnings = new List<object>();
            var payload = BuildSpeechPayload(new SpeechRequest
            {
                Model = request.Model,
                Text = text.ToString(),
                Voice = part.Voice ?? request.Voice,
                Instructions = request.Instructions,
                ProviderOptions = new() { [GoogleExtensions.Identifier()] = JsonSerializer.SerializeToElement(native) }
            }, partWarnings);
            ApplyGoogleStreamingFormat(payload, request.OutputFormat, partWarnings);
            payload["stream"] = true;

            // Compare effective payloads, not raw part options: model voice shortcuts
            // and explicit native multi-speaker configuration may override generic voices.
            if (selected is not null && !JsonNode.DeepEquals(selected, payload))
                throw new NotSupportedException("Google speech requires one effective configuration for all text parts. Use providerOptions.google.input and generation_config.speech_config for native multi-speaker synthesis.");
            if (selected is null)
                foreach (var warning in partWarnings)
                    warnings.Add(warning);
            selected ??= payload;
        }

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        return selected!;
    }

    private static JsonObject GoogleSpeechOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue(GoogleExtensions.Identifier(), out var native) != true || native.ValueKind == JsonValueKind.Null)
            return [];
        if (native.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.google must be an object.", nameof(options));
        return CloneJsonObject(native);
    }

    private static void ApplyGoogleStreamingFormat(JsonObject payload, AudioFormat? format, ICollection<object> warnings)
    {
        if (format is null)
            return;
        ArgumentException.ThrowIfNullOrWhiteSpace(format.Type);
        if (format.Rate is <= 0)
            throw new ArgumentException("Speech output sample rate must be positive.", nameof(format));
        var mime = format.Type.Trim().ToLowerInvariant() switch
        {
            "pcm" or "audio/pcm" or "audio/l16" => "audio/l16",
            "wav" or "wave" or "audio/wav" or "audio/x-wav" => "audio/wav",
            "mp3" or "audio/mp3" or "audio/mpeg" => "audio/mp3",
            "opus" or "audio/opus" or "audio/ogg_opus" => "audio/ogg_opus",
            "alaw" or "audio/alaw" => "audio/alaw",
            "mulaw" or "audio/mulaw" => "audio/mulaw",
            _ => throw new NotSupportedException($"Google speech output format '{format.Type}' is not supported. Use the native response_format passthrough for other encodings.")
        };
        var responseFormat = GoogleSpeechResponseFormat(payload)
            ?? throw new ArgumentException("Google speech response_format must include an audio format.");
        SetDefault("mime_type", JsonValue.Create(mime)!);
        if (format.Rate is { } rate)
            SetDefault("sample_rate", JsonValue.Create(rate)!);

        void SetDefault(string name, JsonNode value)
        {
            if (!responseFormat.ContainsKey(name))
                responseFormat[name] = value;
            else if (!JsonNode.DeepEquals(responseFormat[name], value))
                warnings.Add(new { type = "ignored", feature = name == "mime_type" ? "outputFormat.type" : "outputFormat.rate", reason = "providerOptions.google.response_format takes precedence" });
        }
    }

    private static JsonObject? GoogleSpeechResponseFormat(JsonObject payload)
        => payload["response_format"] switch
        {
            JsonObject obj when obj["type"]?.GetValue<string>() == "audio" => obj,
            JsonArray array => array.OfType<JsonObject>().FirstOrDefault(obj => obj["type"]?.GetValue<string>() == "audio"),
            _ => null
        };
}
