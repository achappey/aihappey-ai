using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AbacusAI;

public sealed partial class AbacusAIProvider
{
    private static JsonObject SpeechPayload(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var instructions = "Read the following text aloud exactly as written, without adding commentary.";
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            instructions += $"\nDelivery instructions: {request.Instructions}";
        if (!string.IsNullOrWhiteSpace(request.Language))
            instructions += $"\nSpeak in {request.Language}.";
        if (request.Speed is { } speed)
        {
            if (!double.IsFinite(speed) || speed <= 0)
                throw new ArgumentOutOfRangeException(nameof(request.Speed));
            instructions += $"\nSpeak at {speed.ToString(System.Globalization.CultureInfo.InvariantCulture)} times normal speed.";
        }
        var payload = MediaPayload(request.Model, new[]
        {
            new { role = "system", content = instructions },
            new { role = "user", content = request.Text }
        });
        payload["modalities"] = new JsonArray("text", "audio");
        payload["audio"] = new JsonObject
        {
            ["voice"] = request.Voice ?? "alloy",
            ["format"] = AudioFormatName(request.OutputFormat)
        };
        MergeMediaOptions(payload, MediaOptions(request.ProviderOptions));
        // Both modalities are required by OpenAI audio models, including their aliases.
        payload["modalities"] = new JsonArray("text", "audio");
        return payload;
    }

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var payload = SpeechPayload(request);
        var (root, headers) = await SendMediaAsync(payload, cancellationToken);
        var message = MediaMessage(root);
        if (!message.TryGetProperty("audio", out var audio) || ReadString(audio, "data") is not { Length: > 0 } data)
            throw new InvalidOperationException("AbacusAI speech response did not contain audio data.");
        var format = ReadString(audio, "format") ?? payload["audio"]!["format"]!.GetValue<string>();
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(Convert.FromBase64String(data)), Format = format, MimeType = AudioMime(format) },
            ProviderMetadata = MediaMetadata(root),
            Response = MediaResponse(root, request.Model, headers),
            Request = new() { Body = payload },
            Warnings = SpeechWarnings(request)
        };
    }

    private static List<object> SpeechWarnings(SpeechRequest request)
        => request.Speed is null ? [] : [new { type = "other", message = "Speech speed is a model instruction, not a guaranteed audio playback rate." }];

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Text is not { Length: > 0 } || request.Text.Any(p => p is null || p.Type != "text" || p.Text is null))
            throw new ArgumentException("Speech requires an ordered array of text parts.");
        var voices = request.Text.Select(p => p.Voice ?? request.Voice ?? "alloy").Distinct().ToArray();
        if (voices.Length > 1)
            throw new NotSupportedException("AbacusAI speech accepts one voice per request.");
        var options = MediaOptions(request.ProviderOptions);
        JsonObject? firstPartOptions = null;
        foreach (var part in request.Text)
        {
            var current = MediaOptions(part.ProviderOptions);
            if (firstPartOptions is not null && !JsonNode.DeepEquals(firstPartOptions, current))
                throw new NotSupportedException("AbacusAI speech requires identical options for all text parts.");
            firstPartOptions ??= current;
        }
        MergeMediaOptions(options, firstPartOptions ?? new());
        if (request.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0))
            throw new ArgumentOutOfRangeException(nameof(request.Speed));
        var speech = new SpeechRequest
        {
            Model = request.Model, Text = string.Concat(request.Text.Select(p => p.Text)), Voice = voices[0],
            OutputFormat = request.OutputFormat?.Type, Instructions = request.Instructions,
            Language = request.Language, Speed = request.Speed,
            ProviderOptions = new() { ["abacusai"] = JsonSerializer.SerializeToElement(options) }
        };
        var payload = SpeechPayload(speech);
        payload["stream"] = true;
        var streamOptions = payload["stream_options"] as JsonObject ?? new JsonObject();
        streamOptions["include_usage"] = true;
        payload["stream_options"] = streamOptions.DeepClone();
        ApplyAuthHeader();
        var warnings = SpeechWarnings(speech);
        if (request.OutputFormat?.Rate is not null)
            warnings.Add(new { type = "unsupported", feature = "outputFormat.rate" });
        yield return new SpeechStreamStartPart { Warnings = warnings };

        var pending = "";
        var format = payload["audio"]!["format"]!.GetValue<string>();
        var finished = false;
        var hasAudio = false;
        var metadataSent = false;
        JsonElement last = default;
        string? id = null;
        string? model = null;
        JsonElement? usage = null;
        await foreach (var item in _client.GetChatCompletionSseEvents(JsonSerializer.SerializeToElement(payload), ct: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Data is null)
                continue;
            using var doc = JsonDocument.Parse(item.Data);
            var root = doc.RootElement;
            ThrowMediaError(root);
            last = root.Clone();
            id ??= ReadString(root, "id");
            model ??= ReadString(root, "model");
            if (!metadataSent)
            {
                metadataSent = true;
                yield return new SpeechResponseMetadataPart
                {
                    Timestamp = MediaResponse(root, request.Model, new Dictionary<string, string>()).Timestamp,
                    ModelId = (ReadString(root, "model") ?? request.Model).ToModelId(GetIdentifier())
                };
            }
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = root.Clone() };
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                usage = u.Clone();
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
            {
                if (finish.GetString() != "stop")
                    throw new InvalidOperationException($"AbacusAI speech terminated with finish_reason '{finish}'.");
                finished = true;
            }
            if (!choice.TryGetProperty("delta", out var delta))
                continue;
            if (ReadString(delta, "refusal") is { Length: > 0 } refusal)
                throw new InvalidOperationException($"AbacusAI speech refused: {refusal}");
            if (!delta.TryGetProperty("audio", out var audio))
                continue;
            var nextFormat = ReadString(audio, "format");
            if (nextFormat is not null)
            {
                if (hasAudio && nextFormat != format)
                    throw new InvalidOperationException("AbacusAI changed audio format within a stream.");
                format = nextFormat;
            }
            var data = ReadString(audio, "data");
            if (string.IsNullOrEmpty(data))
                continue;
            pending += string.Concat(data.Where(c => !char.IsWhiteSpace(c)));
            var count = pending.Length - pending.Length % 4;
            if (count == 0)
                continue;
            var bytes = Convert.FromBase64String(pending[..count]);
            pending = pending[count..];
            hasAudio |= bytes.Length > 0;
            if (bytes.Length > 0)
                yield return new SpeechAudioPart
                {
                    Audio = Convert.ToBase64String(bytes), AudioFormat = new() { Type = AudioMime(format) }
                };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!finished)
            throw new InvalidOperationException("AbacusAI speech stream ended without a completion marker.");
        if (pending.Length > 0)
        {
            // Providers may omit final base64 padding; a one-character tail is always invalid.
            var bytes = Convert.FromBase64String(pending.PadRight((pending.Length + 3) / 4 * 4, '='));
            hasAudio |= bytes.Length > 0;
            yield return new SpeechAudioPart { Audio = Convert.ToBase64String(bytes), AudioFormat = new() { Type = AudioMime(format) } };
        }
        if (!hasAudio)
            throw new InvalidOperationException("AbacusAI speech stream did not contain audio.");
        var final = JsonNode.Parse(last.GetRawText())!.AsObject();
        final["id"] = id;
        final["model"] = model;
        if (usage is { } finalUsage)
            final["usage"] = JsonNode.Parse(finalUsage.GetRawText());
        yield return new SpeechFinishPart
        {
            Usage = new()
            {
                InputTokens = usage is { } input ? ReadInt64(input, "prompt_tokens") : null,
                OutputTokens = usage is { } output ? ReadInt64(output, "completion_tokens") : null,
                Raw = usage?.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            },
            ProviderMetadata = MediaMetadata(JsonSerializer.SerializeToElement(final))
        };
    }
}
