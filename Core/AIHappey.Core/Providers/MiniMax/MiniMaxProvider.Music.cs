using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.MiniMax;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider : IModelProvider
{
    public async Task<SpeechResponse> MusicRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));


        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetSpeechProviderMetadata<MiniMaxSpeechProviderMetadata>(GetIdentifier());

        if (string.IsNullOrWhiteSpace(metadata?.Lyrics))
            throw new ArgumentException("Lyrics are required.", nameof(request));

        // ---- audio_setting ----
        string format = (request.OutputFormat
            ?? metadata?.AudioSetting?.Format
            ?? "mp3").Trim().ToLowerInvariant();

        format = format is "mp3" or "wav" or "pcm" ? format : "mp3";

        // Contract choice: we always request hex, then return a data-url.
        const string outputFormat = "hex";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Text,
            ["lyrics"] = metadata?.Lyrics,
            ["stream"] = false,
            ["output_format"] = outputFormat,
            ["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = format,
                ["sample_rate"] = metadata?.AudioSetting?.SampleRate,
                ["bitrate"] = metadata?.AudioSetting?.Bitrate,
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/music_generation")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax music_generation failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);

        // ---- MiniMax error surface (base_resp) ----
        if (doc.RootElement.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.ValueKind == JsonValueKind.Object &&
            baseResp.TryGetProperty("status_code", out var statusCodeEl) &&
            statusCodeEl.ValueKind == JsonValueKind.Number &&
            statusCodeEl.GetInt32() != 0)
        {
            var traceId = doc.RootElement.TryGetProperty("trace_id", out var traceEl) && traceEl.ValueKind == JsonValueKind.String
                ? traceEl.GetString()
                : null;

            var msg = baseResp.TryGetProperty("status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : "MiniMax request failed";

            throw new InvalidOperationException($"MiniMax music_generation failed (status_code={statusCodeEl.GetInt32()}, status_msg={msg}, trace_id={traceId}).");
        }

        // ---- Extract audio hex ----
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("audio", out var audioEl) ||
            audioEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"MiniMax music_generation response missing data.audio: {raw}");
        }

        var hex = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("MiniMax music_generation returned empty audio.");

        var bytes = DecodeHexStringToBytes(hex);
        var mime = GuessAudioMimeType(format);
        var audioDataUrl = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new ()
            {
                Base64 = audioDataUrl,
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };

    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
