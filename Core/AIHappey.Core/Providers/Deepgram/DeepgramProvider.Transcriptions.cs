using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Deepgram;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    public Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => TranscriptionRequestInternal(request, cancellationToken);

    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        // Incoming pipeline passes base64 (data-url prefix already stripped upstream).
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var metadata = request.GetTranscriptionProviderMetadata<DeepgramTranscriptionProviderMetadata>(GetIdentifier());

        // Build query string.
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(request.Model)}",
        };

        void AddBool(string key, bool? value)
        {
            if (value is null) return;
            query.Add($"{key}={value.Value.ToString().ToLowerInvariant()}");
        }

        void AddString(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            query.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        AddString("language", metadata?.Language);
        AddString("version", metadata?.Version);

        AddBool("punctuate", metadata?.Punctuate);
        AddBool("smart_format", metadata?.SmartFormat);
        AddBool("paragraphs", metadata?.Paragraphs);
        AddBool("utterances", metadata?.Utterances);
        AddBool("diarize", metadata?.Diarize);
        AddBool("multichannel", metadata?.Multichannel);
        AddBool("detect_entities", metadata?.DetectEntities);
        AddBool("topics", metadata?.Topics);
        AddBool("intents", metadata?.Intents);
        AddBool("sentiment", metadata?.Sentiment);
        AddBool("mip_opt_out", metadata?.MipOptOut);

        // detect_language: boolean OR list-of-strings
        if (metadata?.DetectLanguage is { } dl)
        {
            if (dl.ValueKind == JsonValueKind.True || dl.ValueKind == JsonValueKind.False)
            {
                query.Add($"detect_language={dl.GetBoolean().ToString().ToLowerInvariant()}");
            }
            else if (dl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in dl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var lang = el.GetString();
                    if (!string.IsNullOrWhiteSpace(lang))
                        query.Add($"detect_language={Uri.EscapeDataString(lang)}");
                }
            }
            else
            {
                warnings.Add(new { type = "unsupported", feature = "detect_language", reason = "Must be boolean or array of strings." });
            }
        }

        // tag: string OR list-of-strings
        if (metadata?.Tag is { } tag)
        {
            if (tag.ValueKind == JsonValueKind.String)
            {
                var t = tag.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                    query.Add($"tag={Uri.EscapeDataString(t)}");
            }
            else if (tag.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in tag.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var t = el.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                        query.Add($"tag={Uri.EscapeDataString(t)}");
                }
            }
        }

        var url = "v1/listen" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bytes)
        };

        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Deepgram STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertDeepgramResponse(json, request.Model, now, warnings);
    }

    private static TranscriptionResponse ConvertDeepgramResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string transcript = "";
        string? detectedLanguage = null;
        float? duration = null;

        // results.channels[0].alternatives[0].transcript
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
                    transcript = t.GetString() ?? "";
            }
        }

        // metadata.duration
        if (root.TryGetProperty("metadata", out var meta)
            && meta.TryGetProperty("duration", out var dur)
            && dur.ValueKind == JsonValueKind.Number)
        {
            duration = (float)dur.GetDouble();
        }

        // results.utterances[] => segments
        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("results", out var results2)
            && results2.TryGetProperty("utterances", out var utterances)
            && utterances.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in utterances.EnumerateArray())
            {
                var text = u.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? "")
                    : "";

                var start = u.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Number
                    ? (float)s.GetDouble()
                    : 0;

                var end = u.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Number
                    ? (float)e.GetDouble()
                    : 0;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = text,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
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

