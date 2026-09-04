using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.Providers.MiniMax;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);
        ApplyAuthHeader();

        var request = options.ToSpeechRequest();
        var metadata = request.GetProviderMetadata<MiniMaxSpeechProviderMetadata>(GetIdentifier());
        var payload = BuildSpeechPayload(request, metadata, stream: true);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "v1/t2a_v2")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson))
        };
        httpRequest.Content.Headers.ContentType = new("application/json");
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"MiniMax streaming {("speech")} request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        var completed = false;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
                continue;

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            ThrowIfMiniMaxFailed(root, "t2a_v2");

            if (!TryReadStreamAudio(root, out var audioHex, out var status))
                continue;

            if (!string.IsNullOrWhiteSpace(audioHex))
            {
                yield return new AudioSpeechStreamDelta
                {
                    Audio = Convert.ToBase64String(DecodeHexStringToBytes(audioHex))
                };
            }

            if (status == 2)
                completed = true;
        }

        // MiniMax marks the terminal SSE object with status=2. A provider that
        // closes immediately after all audio chunks is also considered complete.
        _ = completed;
        yield return new AudioSpeechStreamDone();
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
    }

    private static bool TryReadStreamAudio(JsonElement root, out string? audio, out int? status)
    {
        audio = null;
        status = null;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return false;

        if (data.TryGetProperty("audio", out var audioElement) && audioElement.ValueKind == JsonValueKind.String)
            audio = audioElement.GetString();

        if (data.TryGetProperty("status", out var statusElement)
            && statusElement.ValueKind == JsonValueKind.Number
            && statusElement.TryGetInt32(out var parsedStatus))
        {
            status = parsedStatus;
        }

        return audio is not null || status is not null;
    }

    private static void ThrowIfMiniMaxFailed(JsonElement root, string endpoint)
    {
        if (!root.TryGetProperty("base_resp", out var baseResponse)
            || baseResponse.ValueKind != JsonValueKind.Object
            || !baseResponse.TryGetProperty("status_code", out var statusCode)
            || statusCode.ValueKind != JsonValueKind.Number
            || statusCode.GetInt32() == 0)
        {
            return;
        }

        var message = baseResponse.TryGetProperty("status_msg", out var statusMessage)
                      && statusMessage.ValueKind == JsonValueKind.String
            ? statusMessage.GetString()
            : "MiniMax request failed";

        throw new InvalidOperationException($"MiniMax {endpoint} failed (status_code={statusCode.GetInt32()}, status_msg={message}).");
    }
}
