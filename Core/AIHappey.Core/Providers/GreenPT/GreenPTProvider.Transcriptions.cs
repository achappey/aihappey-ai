using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.Providers.GreenPT;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    private const string GreenPtListenEndpoint = "v1/listen";

    public Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => TranscriptionRequestInternal(request, cancellationToken);

    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(GreenPT)} API key.");

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var metadata = request.GetProviderMetadata<GreenPTTranscriptionProviderMetadata>(GetIdentifier());

        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(request.Model)}",
        };

        void AddBool(string keyName, bool? value)
        {
            if (value is null) return;
            query.Add($"{keyName}={value.Value.ToString().ToLowerInvariant()}");
        }

        void AddString(string keyName, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            query.Add($"{keyName}={Uri.EscapeDataString(value)}");
        }

        AddString("language", metadata?.Language);
        AddBool("diarize", metadata?.Diarize);
        AddBool("punctuate", metadata?.Punctuate);
        AddBool("smart_format", metadata?.SmartFormat);
        AddBool("filler_words", metadata?.FillerWords);
        AddBool("numerals", metadata?.Numerals);
        AddBool("sentiment", metadata?.Sentiment);
        AddBool("topics", metadata?.Topics);
        AddBool("intents", metadata?.Intents);

        var url = GreenPtListenEndpoint + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bytes)
        };

        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Token", key);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GreenPT STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model, now, warnings);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string transcript = string.Empty;
        string? detectedLanguage = null;
        float? duration = null;

        JsonElement.ArrayEnumerator words = default;
        var hasWords = false;

        if (root.TryGetProperty("results", out var results)
            && results.TryGetProperty("channels", out var channels)
            && channels.ValueKind == JsonValueKind.Array
            && channels.GetArrayLength() > 0)
        {
            var ch0 = channels[0];

            if (ch0.TryGetProperty("detected_language", out var dl) && dl.ValueKind == JsonValueKind.String)
                detectedLanguage = dl.GetString();

            if (ch0.TryGetProperty("alternatives", out var alts)
                && alts.ValueKind == JsonValueKind.Array
                && alts.GetArrayLength() > 0)
            {
                var alt0 = alts[0];

                if (alt0.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String)
                    transcript = t.GetString() ?? string.Empty;

                if (alt0.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
                {
                    words = wordsEl.EnumerateArray();
                    hasWords = true;
                }
            }
        }

        if (root.TryGetProperty("metadata", out var meta)
            && meta.TryGetProperty("duration", out var dur)
            && dur.ValueKind == JsonValueKind.Number)
        {
            duration = (float)dur.GetDouble();
        }

        var segments = new List<TranscriptionSegment>();

        if (hasWords)
        {
            int? currentSpeaker = null;
            float segmentStart = 0;
            float segmentEnd = 0;
            var segmentWords = new List<string>();

            foreach (var word in words)
            {
                var text = word.TryGetProperty("word", out var w) && w.ValueKind == JsonValueKind.String
                    ? w.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var start = word.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number
                    ? (float)s.GetDouble()
                    : segmentEnd;

                var end = word.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number
                    ? (float)e.GetDouble()
                    : start;

                int? speaker = null;
                if (word.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.Number)
                    speaker = sp.GetInt32();

                if (segmentWords.Count == 0)
                {
                    currentSpeaker = speaker;
                    segmentStart = start;
                    segmentEnd = end;
                    segmentWords.Add(text!);
                    continue;
                }

                if (speaker != currentSpeaker)
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = string.Join(" ", segmentWords),
                        StartSecond = segmentStart,
                        EndSecond = segmentEnd
                    });

                    segmentWords.Clear();
                    currentSpeaker = speaker;
                    segmentStart = start;
                    segmentEnd = end;
                    segmentWords.Add(text!);
                    continue;
                }

                segmentEnd = end;
                segmentWords.Add(text!);
            }

            if (segmentWords.Count > 0)
            {
                segments.Add(new TranscriptionSegment
                {
                    Text = string.Join(" ", segmentWords),
                    StartSecond = segmentStart,
                    EndSecond = segmentEnd
                });
            }
        }

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(transcript))
        {
            segments.Add(new TranscriptionSegment
            {
                Text = transcript,
                StartSecond = 0,
                EndSecond = duration ?? 0
            });
        }

        return new TranscriptionResponse
        {
            Text = transcript,
            Language = detectedLanguage,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = json,
            }
        };
    }
}

