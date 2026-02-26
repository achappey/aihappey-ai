using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Kugu;

public partial class KuguProvider
{
    private const int MaxPollAttempts = 180;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions KuguSpeechJson = new(JsonSerializerDefaults.Web)
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

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var modelId = NormalizeModelId(request.Model);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["text"] = request.Text,
            ["voice"] = string.IsNullOrWhiteSpace(request.Voice) ? null : request.Voice.Trim(),
            ["language"] = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim()
        };

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, KuguSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var createBody = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS create failed ({(int)createResp.StatusCode}): {createBody}");

        var (jobId, createStatus, initialAudioUrl) = ParseCreateSpeechResponse(createBody);
        var (finalStatus, finalBody, audioUrl) = await ResolveAudioUrlAsync(jobId, createStatus, initialAudioUrl, createBody, cancellationToken);

        var audioBytes = await DownloadAudioAsync(audioUrl, cancellationToken);
        var outputFormat = ResolveFormat(request.OutputFormat, audioUrl);
        var mime = ResolveMimeType(outputFormat);

        var providerMetaPayload = new
        {
            model = modelId,
            requestedVoice = request.Voice,
            requestedLanguage = request.Language,
            status = finalStatus,
            jobId,
            audioUrl,
            bytes = audioBytes.Length,
            create = TryParseJsonElement(createBody),
            statusPayload = TryParseJsonElement(finalBody)
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mime,
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetaPayload)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMetaPayload)
            }
        };
    }

    private static (string? JobId, string? Status, string? AudioUrl) ParseCreateSpeechResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = ReadString(root, "id");
        var status = ReadString(root, "status");
        var audioUrl = TryReadOutputAudioUrl(root);

        return (id, status, audioUrl);
    }

    private async Task<(string Status, string FinalBody, string AudioUrl)> ResolveAudioUrlAsync(
        string? jobId,
        string? createStatus,
        string? initialAudioUrl,
        string createBody,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(initialAudioUrl))
            return (createStatus ?? "completed", createBody, initialAudioUrl);

        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException($"{ProviderName} TTS response has neither output.audio_url nor id: {createBody}");

        var terminal = await PollUntilTerminalAsync(jobId, cancellationToken);
        var audioUrl = TryReadOutputAudioUrl(terminal.Root);
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"{ProviderName} job '{jobId}' completed without output.audio_url: {terminal.Raw}");

        return (terminal.Status, terminal.Raw, audioUrl);
    }

    private async Task<(string Status, string Raw, JsonElement Root)> PollUntilTerminalAsync(string jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            using var statusResp = await _client.GetAsync($"v1/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
            var statusRaw = await statusResp.Content.ReadAsStringAsync(cancellationToken);
            if (!statusResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} job status failed ({(int)statusResp.StatusCode}): {statusRaw}");

            using var statusDoc = JsonDocument.Parse(statusRaw);
            var root = statusDoc.RootElement.Clone();
            var status = ReadString(root, "status") ?? "unknown";

            if (IsCompletedStatus(status))
                return (status, statusRaw, root);

            if (IsFailedStatus(status))
                throw new InvalidOperationException($"{ProviderName} TTS job failed. JobId={jobId}, status={status}, body={statusRaw}");

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException($"{ProviderName} TTS job polling exceeded {MaxPollAttempts} attempts. JobId={jobId}");
    }

    private async Task<byte[]> DownloadAudioAsync(string audioUrl, CancellationToken cancellationToken)
    {
        using var audioResp = await _client.GetAsync(audioUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return bytes;
    }

    private static string NormalizeModelId(string model)
    {
        var normalized = model.Trim();
        var prefix = ProviderId + "/";

        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return normalized[prefix.Length..];

        return normalized;
    }

    private static bool IsCompletedStatus(string status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedStatus(string status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadOutputAudioUrl(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "output", out var output)
            || output.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(output, "audio_url");
    }

    private static JsonElement? TryParseJsonElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveFormat(string? requestedOutputFormat, string audioUrl)
    {
        var requested = requestedOutputFormat?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested switch
            {
                "mpeg" => "mp3",
                "wave" => "wav",
                _ => requested
            };
        }

        if (Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;
            var dot = path.LastIndexOf('.');
            if (dot > -1 && dot < path.Length - 1)
            {
                var ext = path[(dot + 1)..].Trim().ToLowerInvariant();
                return ext switch
                {
                    "mpeg" => "mp3",
                    "wave" => "wav",
                    _ => ext
                };
            }
        }

        return "mp3";
    }

    private static string ResolveMimeType(string format)
        => format switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            _ => "audio/mpeg"
        };

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

