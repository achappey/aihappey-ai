using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WisdomGate;

public partial class WisdomGateProvider
{
    private sealed record WisdomGateVideoStatus(string Status, JsonElement Root);

    private async Task<VideoResponse> WisdomGateVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        using var createContent = BuildVideoCreateContent(request, metadata, warnings);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = createContent
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"WisdomGate video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();

        var videoId = createRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("WisdomGate video create did not return an id.");

        var pollIntervalSeconds = WgVideoTryGetInt(metadata, "poll_interval_seconds") ?? 5;
        var pollTimeoutMinutes = WgVideoTryGetInt(metadata, "poll_timeout_minutes") ?? 10;
        var pollMaxAttempts = WgVideoTryGetInt(metadata, "poll_max_attempts");

        var terminal = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollVideoAsync(videoId, ct),
            isTerminal: s => IsTerminalVideoStatus(s.Status),
            interval: TimeSpan.FromSeconds(Math.Max(1, pollIntervalSeconds)),
            timeout: TimeSpan.FromMinutes(Math.Max(1, pollTimeoutMinutes)),
            maxAttempts: pollMaxAttempts,
            cancellationToken: cancellationToken);

        if (!string.Equals(terminal.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var err = TryGetVideoError(terminal.Root);
            throw new InvalidOperationException($"WisdomGate video generation failed with status '{terminal.Status}': {err}");
        }

        using var contentReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{videoId}/content");
        using var contentResp = await _client.SendAsync(contentReq, cancellationToken);
        var bytes = await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"WisdomGate video download failed ({(int)contentResp.StatusCode}): {text}");
        }

        var mediaType = contentResp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
            mediaType = "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    create = createRoot,
                    final = terminal.Root,
                    id = videoId,
                    status = terminal.Status,
                    progress = TryGetProgress(terminal.Root)
                }, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    id = videoId,
                    status = terminal.Status,
                    mediaType,
                    bytes = bytes.Length
                }
            }
        };
    }

    private static HttpContent BuildVideoCreateContent(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Prompt), "prompt");
        form.Add(new StringContent(request.Model), "model");

        var seconds = request.Duration?.ToString(CultureInfo.InvariantCulture)
            ?? WgVideoTryGetString(metadata, "seconds");
        if (!string.IsNullOrWhiteSpace(seconds))
            form.Add(new StringContent(seconds), "seconds");

        var size = request.Resolution
            ?? WgVideoTryGetString(metadata, "size")
            ?? WgVideoTryGetString(metadata, "resolution");
        if (!string.IsNullOrWhiteSpace(size))
            form.Add(new StringContent(size), "size");

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "WisdomGate /v1/videos expects size and does not expose aspect ratio directly." });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Image is not null)
        {
            var imageBytes = Convert.FromBase64String(request.Image.Data.RemoveDataUrlPrefix());
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(request.Image.MediaType) ? "image/png" : request.Image.MediaType);
            form.Add(imageContent, "input_reference", "input_reference");
        }

        return form;
    }

    private async Task<WisdomGateVideoStatus> PollVideoAsync(string videoId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{videoId}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"WisdomGate video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "queued"
            : "queued";

        return new WisdomGateVideoStatus(status, root);
    }

    private static bool IsTerminalVideoStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetVideoError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
            return errorEl.GetString() ?? "Unknown error";

        if (root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
            return messageEl.GetString() ?? "Unknown error";

        return "Unknown error";
    }

    private static int? TryGetProgress(JsonElement root)
    {
        if (root.TryGetProperty("progress", out var progressEl) && progressEl.ValueKind == JsonValueKind.Number && progressEl.TryGetInt32(out var value))
            return value;

        return null;
    }

    private static string? WgVideoTryGetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? WgVideoTryGetInt(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
            : null;
}

