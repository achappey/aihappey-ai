using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.ModelsLab;

public partial class ModelsLabProvider
{
    private static readonly HashSet<string> UltraVideoModels =
    [
        "wan2.1",
        "wan2.2"
    ];

    private async Task<VideoResponse> VideoRequestTextToVideo(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var warnings = new List<object>();
        var now = DateTime.UtcNow;

        if (request.Image is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "image"
            });
        }

        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No {nameof(ModelsLab)} API key.");

        var effectiveModelId = request.Model;

        var payload = new Dictionary<string, object?>
        {
            ["key"] = apiKey,
            ["model_id"] = effectiveModelId,
            ["prompt"] = request.Prompt
        };

        MergeRawProviderOptions(payload, request.ProviderOptions);

        // Keep reserved keys deterministic after raw passthrough merge.
        payload["key"] = apiKey;
        payload["model_id"] = effectiveModelId;
        payload["prompt"] = request.Prompt;

        var endpoint = ResolveVideoGenerationEndpoint(effectiveModelId);
        var startResponse = await PostModelsLabVideoJsonAsync(endpoint, payload, cancellationToken);

        ThrowIfModelsLabVideoError(startResponse, "video generation");

        JsonElement completed = startResponse.Clone();
        var status = GetStatus(completed);
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var requestId = GetRequestId(completed)
                ?? throw new InvalidOperationException("ModelsLab video request did not return an id for polling.");

            completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => FetchVideoStatusAsync(requestId, apiKey, ct),
                isTerminal: root => IsVideoTerminalStatus(GetStatus(root)),
                interval: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromMinutes(10),
                maxAttempts: null,
                cancellationToken: cancellationToken);

            ThrowIfModelsLabVideoError(completed, "video fetch");

            var finalStatus = GetStatus(completed);
            if (!string.Equals(finalStatus, "success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ModelsLab video generation did not complete successfully. Status: {finalStatus ?? "unknown"}.");
        }

        var outputValue = GetFirstOutputValue(completed)
            ?? throw new InvalidOperationException("ModelsLab video generation returned no output.");

        var (base64Data, mediaType) = await ResolveVideoOutputAsync(outputValue, cancellationToken);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = completed.Clone()
        };

        if (completed.TryGetProperty("meta", out var metaEl))
            providerMetadata["meta"] = metaEl.Clone();

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    Data = base64Data,
                    MediaType = mediaType
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed.Clone()
            }
        };
    }

    private static string ResolveVideoGenerationEndpoint(string modelId)
        => UltraVideoModels.Contains(modelId) ? "api/v6/video/text2video_ultra" : "api/v6/video/text2video";

    private async Task<JsonElement> PostModelsLabVideoJsonAsync(string endpoint, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"ModelsLab API error: {(int)resp.StatusCode} {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> FetchVideoStatusAsync(long requestId, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["key"] = apiKey
        };

        return await PostModelsLabVideoJsonAsync($"api/v6/video/fetch/{requestId}", payload, cancellationToken);
    }

    private static bool IsVideoTerminalStatus(string? status)
        => string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static string? GetStatus(JsonElement root)
        => root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

    private static long? GetRequestId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idEl))
            return null;

        if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var idNumber))
            return idNumber;

        if (idEl.ValueKind == JsonValueKind.String && long.TryParse(idEl.GetString(), out var idString))
            return idString;

        return null;
    }

    private static void ThrowIfModelsLabVideoError(JsonElement root, string operation)
    {
        var status = GetStatus(root);
        if (!string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return;

        var message = root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
            ? messageEl.GetString()
            : "Unknown ModelsLab error.";

        throw new Exception($"ModelsLab {operation} error: {message}");
    }

    private static string? GetFirstOutputValue(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputEl) || outputEl.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in outputEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                continue;

            var value = entry.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private async Task<(string Base64Data, string MediaType)> ResolveVideoOutputAsync(string output, CancellationToken cancellationToken)
    {
        if (output.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase))
        {
            var mediaTypeEnd = output.IndexOf(';');
            var commaIndex = output.IndexOf(',');

            if (mediaTypeEnd > 5 && commaIndex > mediaTypeEnd)
            {
                var mediaType = output[5..mediaTypeEnd];
                var dataPart = output[(commaIndex + 1)..];
                return (dataPart, string.IsNullOrWhiteSpace(mediaType) ? "video/mp4" : mediaType);
            }
        }

        if (Uri.TryCreate(output, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var resp = await _client.GetAsync(uri, cancellationToken);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                var err = Encoding.UTF8.GetString(bytes);
                throw new Exception($"ModelsLab video download failed: {(int)resp.StatusCode} {resp.StatusCode}: {err}");
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(output)
                ?? "video/mp4";

            return (Convert.ToBase64String(bytes), mediaType);
        }

        return (output, "video/mp4");
    }

    private static string? GuessVideoMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var withoutQuery = value.Split('?', '#')[0];
        if (withoutQuery.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (withoutQuery.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (withoutQuery.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (withoutQuery.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";

        return null;
    }
}
