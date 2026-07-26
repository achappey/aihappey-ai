using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.HumeAI;

public partial class HumeAIProvider
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
        if (!string.Equals(options.StreamFormat?.Trim(), "sse", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("HumeAI streaming speech requires stream_format 'sse'.");

        var (payload, _, _) = BuildSpeechPayload(options.ToSpeechRequest(), streaming: true);
        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v0/tts/stream/json")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, HumeJsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"{ProviderName} streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? eventName = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (line.Length == 0)
            {
                var streamEvent = ParseHumeSpeechStreamEvent(eventName, dataLines);
                eventName = null;
                dataLines.Clear();

                if (streamEvent is not null)
                    yield return streamEvent;
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        var finalEvent = ParseHumeSpeechStreamEvent(eventName, dataLines);
        if (finalEvent is not null)
            yield return finalEvent;

        cancellationToken.ThrowIfCancellationRequested();
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

    private static IAudioSpeechStreamEvent? ParseHumeSpeechStreamEvent(
        string? eventName,
        IReadOnlyList<string> dataLines)
    {
        if (dataLines.Count == 0)
            return null;

        var data = string.Join('\n', dataLines);
        if (string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(eventName, "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"HumeAI streaming TTS failed: {data}");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse HumeAI speech SSE event: {data}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var type = HumeReadString(root, "type");
            if (string.Equals(type, "timestamp", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"HumeAI streaming TTS failed: {root.GetRawText()}");
            if (!string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(type)
                        ? $"HumeAI speech SSE event did not include a type: {data}"
                        : $"Unsupported HumeAI speech SSE event type '{type}'.");

            var audio = HumeReadString(root, "audio");
            if (string.IsNullOrWhiteSpace(audio))
                throw new InvalidOperationException("HumeAI speech SSE audio event did not include audio data.");

            try
            {
                return new AudioSpeechStreamDelta
                {
                    Audio = Convert.ToBase64String(Convert.FromBase64String(audio))
                };
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "HumeAI speech SSE audio event contained invalid base64 audio data.", ex);
            }
        }
    }
}
