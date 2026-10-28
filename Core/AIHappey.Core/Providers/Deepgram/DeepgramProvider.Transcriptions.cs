using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));
        var options = ListenOptions.Create(request.Model, request.ProviderOptions, streaming: false);
        var audio = request.Audio?.ToString();
        var remoteAudioUrl = GetRemoteAudioUrl(audio);
        var bytes = remoteAudioUrl is null ? DecodeTranscriptionAudio(audio) : null;
        if (remoteAudioUrl is null)
            options.ValidateAudio(request.MediaType, null);
        var key = ResolveTranscriptionKey();
        var now = DateTime.UtcNow;
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/listen?" + options.Query)
        {
            Content = remoteAudioUrl is null
                ? new ByteArrayContent(bytes!)
                : new StringContent(JsonSerializer.Serialize(new { url = remoteAudioUrl }), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Token", key);
        if (remoteAudioUrl is null)
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _client.SendAsync(message, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Deepgram STT failed ({(int)response.StatusCode}): {json}");
        using var document = JsonDocument.Parse(json);
        var normalized = NormalizeListenResponse(document.RootElement);
        return new TranscriptionResponse
        {
            Text = normalized.Text,
            Language = normalized.Language,
            DurationInSeconds = (float?)normalized.Duration,
            Segments = (options.WordOnly ? normalized.Words : normalized.Segments).Select(ToSegment).ToArray(),
            ProviderMetadata = ListenMetadata(document.RootElement),
            Warnings = options.Warnings,
            Response = new() { Timestamp = now, ModelId = request.Model, Body = json }
        };
    }

    private string ResolveTranscriptionKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        return string.IsNullOrWhiteSpace(key) ? throw new InvalidOperationException("No Deepgram API key.") : key;
    }

    private static byte[] DecodeTranscriptionAudio(string? audio)
    {
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.");
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0 || !audio[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Audio data URL must contain base64 data.");
            audio = audio[(comma + 1)..];
        }
        var bytes = Convert.FromBase64String(audio);
        return bytes.Length == 0 ? throw new ArgumentException("Audio must not be empty.") : bytes;
    }

    private static string? GetRemoteAudioUrl(string? audio)
    {
        if (string.IsNullOrWhiteSpace(audio)
            || !audio.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !audio.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(audio, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString()
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Audio URL must be a valid absolute HTTP(S) URL.", nameof(audio));
        }

        return audio;
    }

    private static Dictionary<string, JsonElement> ListenMetadata(JsonElement root)
        => new() { ["deepgram"] = root.Clone() };

    private sealed record ListenSpan(string Text, double Start, double End, int Channel, string? Speaker);
    private sealed record ListenResult(string Text, string? Language, double? Duration,
        List<ListenSpan> Words, List<ListenSpan> Segments);

    private static string? ListenString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ListenNumber(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static TranscriptionSegment ToSegment(ListenSpan span)
        => new() { Text = span.Text, StartSecond = (float)span.Start, EndSecond = (float)span.End };

    private static ListenResult NormalizeListenResponse(JsonElement root)
    {
        var words = new List<ListenSpan>();
        var segments = new List<ListenSpan>();
        var texts = new List<string>();
        var languages = new List<string>();
        double? duration = root.TryGetProperty("metadata", out var metadata) ? ListenNumber(metadata, "duration") : null;
        if (root.TryGetProperty("results", out var results))
        {
            if (results.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var channel in channels.EnumerateArray())
                {
                    var language = ListenString(channel, "detected_language");
                    if (language != null) languages.Add(language);
                    if (channel.TryGetProperty("alternatives", out var alternatives) && alternatives.GetArrayLength() > 0)
                    {
                        var alternative = alternatives[0];
                        var text = ListenString(alternative, "transcript");
                        if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                        ReadListenWords(alternative, index, words);
                        if (alternative.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Array)
                            languages.AddRange(langs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                        if (alternative.TryGetProperty("paragraphs", out var paragraphs)
                            && paragraphs.TryGetProperty("paragraphs", out var items))
                            foreach (var paragraph in items.EnumerateArray())
                                if (paragraph.TryGetProperty("sentences", out var sentences))
                                    foreach (var sentence in sentences.EnumerateArray())
                                        AddListenSpan(sentence, "text", index, segments);
                        if (!segments.Any(s => s.Channel == index) && !words.Any(w => w.Channel == index) && !string.IsNullOrWhiteSpace(text))
                            segments.Add(new(text, 0, duration ?? 0, index, null));
                    }
                    index++;
                }
            }
            if (results.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array && utterances.GetArrayLength() > 0)
            {
                segments.Clear();
                foreach (var utterance in utterances.EnumerateArray())
                    AddListenSpan(utterance, "transcript", (int)(ListenNumber(utterance, "channel") ?? 0), segments);
            }
        }
        // Without utterances/paragraphs, group adjacent words by channel and speaker.
        foreach (var channel in words.GroupBy(w => w.Channel))
        {
            if (segments.Any(s => s.Channel == channel.Key)) continue;
            ListenSpan? current = null;
            foreach (var word in channel.OrderBy(w => w.Start))
            {
                if (current != null && current.Speaker == word.Speaker)
                    current = current with { Text = current.Text + " " + word.Text, End = word.End };
                else
                {
                    if (current != null) segments.Add(current);
                    current = word;
                }
            }
            if (current != null) segments.Add(current);
        }
        var distinctLanguages = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(string.Join("\n", texts), distinctLanguages.Length > 1 ? "multi" : distinctLanguages.FirstOrDefault(),
            duration, SortListenSpans(words), SortListenSpans(segments));
    }

    private static List<ListenSpan> SortListenSpans(IEnumerable<ListenSpan> spans)
        => spans.OrderBy(s => s.Start).ThenBy(s => s.Channel).ThenBy(s => s.End).ToList();

    private static void ReadListenWords(JsonElement alternative, int channel, List<ListenSpan> words)
    {
        if (!alternative.TryGetProperty("words", out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var word in items.EnumerateArray())
            AddListenSpan(word, ListenString(word, "punctuated_word") is { Length: > 0 } ? "punctuated_word" : "word", channel, words);
    }

    private static void AddListenSpan(JsonElement item, string textKey, int channel, List<ListenSpan> target)
    {
        var text = ListenString(item, textKey);
        if (string.IsNullOrWhiteSpace(text)) return;
        var speaker = item.TryGetProperty("speaker", out var value) ? value.ToString() : null;
        target.Add(new(text, ListenNumber(item, "start") ?? 0, ListenNumber(item, "end") ?? 0, channel, speaker));
    }
}
