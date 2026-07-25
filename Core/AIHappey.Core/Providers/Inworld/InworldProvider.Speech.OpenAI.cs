using System.Buffers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Inworld;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));

        var request = options.ToSpeechRequest();
        var metadata = request.GetProviderMetadata<InworldSpeechProviderMetadata>(GetIdentifier());
        var (modelId, modelVoiceId) = ParseInworldSpeechModelAndVoice(request.Model);
        var voiceId = (modelVoiceId ?? request.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Inworld TTS requires a voiceId. Provide AudioSpeechRequest.voice or use an Inworld speech shortcut model id.", nameof(options));

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voiceId"] = voiceId,
            ["modelId"] = modelId,
            ["audioConfig"] = BuildAudioConfig(request, metadata),
            ["timestampType"] = metadata?.TimestampType,
            ["timestampTransportStrategy"] = metadata?.TimestampType is null ? null : "ASYNC",
            ["applyTextNormalization"] = metadata?.ApplyTextNormalization
        };

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/v1/voice:stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, InworldSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Inworld streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var envelope in ReadInworldSpeechStreamEnvelopesAsync(stream, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (envelope.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                throw new InvalidOperationException($"Inworld streaming TTS failed: {errorElement.GetRawText()}");
            }

            if (!envelope.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("audioContent", out var audioContent)
                || audioContent.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var audio = audioContent.GetString();
            if (!string.IsNullOrWhiteSpace(audio))
                yield return new AudioSpeechStreamDelta { Audio = audio };
        }

        yield return new AudioSpeechStreamDone();
    }

    private static async IAsyncEnumerable<JsonElement> ReadInworldSpeechStreamEnvelopesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var pending = new ArrayBufferWriter<byte>();

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            pending.Write(buffer.AsSpan(0, read));
            var reader = new Utf8JsonReader(pending.WrittenSpan, isFinalBlock: false, state: default);
            var consumed = 0L;

            while (true)
            {
                var start = reader.BytesConsumed;
                JsonElement? envelope = null;
                try
                {
                    if (!JsonDocument.TryParseValue(ref reader, out var document))
                        break;

                    using (document)
                        envelope = document.RootElement.Clone();

                    consumed = reader.BytesConsumed;
                }
                catch (JsonException)
                {
                    if (start == 0)
                        throw;

                    consumed = start;
                    break;
                }

                if (envelope is { } value)
                    yield return value;
            }

            if (consumed > 0)
            {
                var remaining = pending.WrittenSpan[(int)consumed..].ToArray();
                pending = new ArrayBufferWriter<byte>(Math.Max(remaining.Length, buffer.Length));
                pending.Write(remaining);
            }
        }

        foreach (var value in pending.WrittenSpan)
        {
            if (char.IsWhiteSpace((char)value))
                continue;

            throw new InvalidOperationException("Inworld streaming TTS returned an incomplete JSON envelope.");
        }
    }

}
