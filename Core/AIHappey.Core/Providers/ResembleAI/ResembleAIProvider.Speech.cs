using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.ResembleAI;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Unified knobs not supported by Resemble's synchronous synth API.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetSpeechProviderMetadata<ResembleAISpeechProviderMetadata>(GetIdentifier());

        // Required: voice_uuid
        var voiceUuid = (request.Voice ?? metadata?.VoiceUuid)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceUuid))
            throw new ArgumentException("ResembleAI requires a voice UUID. Provide SpeechRequest.voice or providerOptions.resembleai.voice_uuid.", nameof(request));

        var outputFormat = NormalizeResembleOutputFormat(
            request.OutputFormat
            ?? metadata?.OutputFormat);

        // Build request body.
        var payload = new Dictionary<string, object?>
        {
            ["voice_uuid"] = voiceUuid,
            ["data"] = request.Text,
            ["model"] = request.Model,
        };

        if (!string.IsNullOrWhiteSpace(metadata?.ProjectUuid))
            payload["project_uuid"] = metadata.ProjectUuid;
        if (!string.IsNullOrWhiteSpace(metadata?.Title))
            payload["title"] = metadata.Title;

        if (!string.IsNullOrWhiteSpace(metadata?.Precision))
            payload["precision"] = metadata.Precision;
        if (!string.IsNullOrWhiteSpace(outputFormat))
            payload["output_format"] = outputFormat;
        if (metadata?.SampleRate is not null)
            payload["sample_rate"] = metadata.SampleRate.Value;
        if (metadata?.UseHd is not null)
            payload["use_hd"] = metadata.UseHd.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("https://f.cluster.resemble.ai/synthesize"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ResembleAI TTS failed ({(int)resp.StatusCode}): {body}");

        // Response shape:
        // { success: true, audio_content: "<base64>", output_format: "wav"|"mp3", sample_rate: 48000, ... }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var audioBase64 = root.TryGetProperty("audio_content", out var ac) && ac.ValueKind == JsonValueKind.String
            ? (ac.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"ResembleAI TTS returned no audio_content. Body: {body}");

        var returnedFormat = root.TryGetProperty("output_format", out var of) && of.ValueKind == JsonValueKind.String
            ? of.GetString()
            : null;

        var effectiveFormat = NormalizeResembleOutputFormat(returnedFormat ?? outputFormat) ?? "wav";
        var mime = effectiveFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = effectiveFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static string? NormalizeResembleOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        var fmt = outputFormat.Trim().ToLowerInvariant();

        // Accept common aliases.
        if (fmt is "mpeg")
            fmt = "mp3";
        if (fmt is "wave")
            fmt = "wav";

        return fmt switch
        {
            "wav" => "wav",
            "mp3" => "mp3",
            _ => fmt // pass-through unknowns so Resemble can validate and return a helpful error
        };
    }
}

