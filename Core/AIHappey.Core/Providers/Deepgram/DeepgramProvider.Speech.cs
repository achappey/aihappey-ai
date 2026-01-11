using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Deepgram;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Deepgram TTS does not accept these in the REST API request shape we support.
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetSpeechProviderMetadata<DeepgramSpeechProviderMetadata>(GetIdentifier());


        // Default Deepgram output: mp3 @ 22050 Hz (not configurable)
        var (encodingFromFormat, containerFromFormat) = MapOutputFormatToDeepgram(request.OutputFormat);

        var encoding = (metadata?.Encoding ?? encodingFromFormat ?? "mp3").Trim();
        var container = (metadata?.Container ?? containerFromFormat)?.Trim();
        var sampleRate = metadata?.SampleRate;
        var bitRate = metadata?.BitRate;

        // Build query string.
        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(request.Model)}",
        };

        if (!string.IsNullOrWhiteSpace(encoding))
            query.Add($"encoding={Uri.EscapeDataString(encoding)}");
        if (!string.IsNullOrWhiteSpace(container))
            query.Add($"container={Uri.EscapeDataString(container!)}");
        if (sampleRate is not null)
            query.Add($"sample_rate={sampleRate.Value}");
        if (bitRate is not null)
            query.Add($"bit_rate={bitRate.Value}");
        if (metadata?.MipOptOut is not null)
            query.Add($"mip_opt_out={metadata.MipOptOut.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(metadata?.Callback))
            query.Add($"callback={Uri.EscapeDataString(metadata.Callback)}");
        if (!string.IsNullOrWhiteSpace(metadata?.CallbackMethod))
            query.Add($"callback_method={Uri.EscapeDataString(metadata.CallbackMethod)}");

        if (metadata?.Tag is not null)
        {
            // Deepgram accepts: tag=<string> OR tag=<string>&tag=<string>...
            if (metadata.Tag.Value.ValueKind == JsonValueKind.String)
            {
                var tag = metadata.Tag.Value.GetString();
                if (!string.IsNullOrWhiteSpace(tag))
                    query.Add($"tag={Uri.EscapeDataString(tag)}");
            }
            else if (metadata.Tag.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in metadata.Tag.Value.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    var tag = el.GetString();
                    if (!string.IsNullOrWhiteSpace(tag))
                        query.Add($"tag={Uri.EscapeDataString(tag)}");
                }
            }
        }

        var url = "v1/speak" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        var body = new { text = request.Text };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        // Avoid mutating shared HttpClient headers.
        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Deepgram TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var mime = ResolveSpeechMimeType(contentType, encoding, container, request.OutputFormat);

        var audio = Convert.ToBase64String(bytes).ToDataUrl(mime);

        // Echo the effective knobs so callers can introspect what was sent.
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(request.Model, JsonSerializerOptions.Web),
            ["encoding"] = JsonSerializer.SerializeToElement(encoding, JsonSerializerOptions.Web),
        };

        if (!string.IsNullOrWhiteSpace(container))
            providerMetadata["container"] = JsonSerializer.SerializeToElement(container, JsonSerializerOptions.Web);
        if (sampleRate is not null)
            providerMetadata["sample_rate"] = JsonSerializer.SerializeToElement(sampleRate.Value, JsonSerializerOptions.Web);
        if (bitRate is not null)
            providerMetadata["bit_rate"] = JsonSerializer.SerializeToElement(bitRate.Value, JsonSerializerOptions.Web);
        if (metadata?.MipOptOut is not null)
            providerMetadata["mip_opt_out"] = JsonSerializer.SerializeToElement(metadata.MipOptOut.Value, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(metadata?.Callback))
            providerMetadata["callback"] = JsonSerializer.SerializeToElement(metadata.Callback, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(metadata?.CallbackMethod))
            providerMetadata["callback_method"] = JsonSerializer.SerializeToElement(metadata.CallbackMethod, JsonSerializerOptions.Web);
        if (metadata?.Tag is not null)
            providerMetadata["tag"] = metadata.Tag.Value;

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = audio,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            }
        };
    }


    private static (string? Encoding, string? Container) MapOutputFormatToDeepgram(string? outputFormat)
    {
        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();

        // Default: mp3
        if (string.IsNullOrWhiteSpace(fmt) || fmt is "mp3" or "mpeg")
            return ("mp3", null);

        if (fmt is "wav" or "wave")
            return ("linear16", "wav");

        if (fmt is "ogg")
            return ("opus", "ogg");

        if (fmt is "opus")
            return ("opus", "ogg");

        if (fmt is "aac")
            return ("aac", null);

        if (fmt is "flac")
            return ("flac", null);

        // Unknown: fall back to mp3.
        return ("mp3", null);
    }

    private static string ResolveSpeechMimeType(string? contentType, string encoding, string? container, string? outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (fmt is "mp3" or "mpeg")
            return "audio/mpeg";
        if (fmt is "wav" or "wave")
            return "audio/wav";
        if (fmt is "ogg" or "opus")
            return "audio/ogg";
        if (fmt is "aac")
            return "audio/aac";
        if (fmt is "flac")
            return "audio/flac";

        var enc = (encoding ?? string.Empty).Trim().ToLowerInvariant();
        return enc switch
        {
            "mp3" => "audio/mpeg",
            "linear16" => (container ?? string.Empty).Equals("wav", StringComparison.OrdinalIgnoreCase)
                ? "audio/wav"
                : "audio/L16",
            "opus" => "audio/ogg",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }
}

