using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AgnesAI;

public partial class AgnesAIProvider
{
    private async Task<VideoResponse> AgnesVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration", details = "Agnes video generation uses num_frames rather than a generic duration field. Use providerOptions.agnesai.num_frames when needed." });

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Agnes video generation docs do not define a generic output count parameter." });

        var payload = CreateAgnesPayload(
            metadata,
            "mode",
            "image",
            "images",
            "image_url",
            "imageUrl",
            "image_urls",
            "imageUrls",
            "extra_body",
            "extraBody",
            "poll_interval_seconds",
            "pollIntervalSeconds",
            "poll_timeout_minutes",
            "pollTimeoutMinutes",
            "poll_max_attempts",
            "pollMaxAttempts");

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        if (request.Fps is not null)
            payload["frame_rate"] = request.Fps.Value;

        var resolvedSize = ResolveAgnesVideoSize(request, metadata, warnings);
        if (resolvedSize is { } size)
        {
            payload["width"] = size.width;
            payload["height"] = size.height;
        }

        var extraBody = CreateAgnesExtraBody(metadata, "image", "images", "image_urls", "imageUrls", "mode");
        var imageUrls = ResolveAgnesVideoInputUrls(request, metadata, warnings);
        var mode = ResolveAgnesVideoMode(metadata);

        if (imageUrls.Count == 1 && extraBody.Count == 0 && !string.Equals(mode, "keyframes", StringComparison.OrdinalIgnoreCase))
        {
            payload["image"] = imageUrls[0];
        }
        else if (imageUrls.Count > 0)
        {
            extraBody["image"] = imageUrls;
        }

        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (extraBody.Count > 0 || string.Equals(mode, "keyframes", StringComparison.OrdinalIgnoreCase) || imageUrls.Count > 1)
                extraBody["mode"] = mode;
            else
                payload["mode"] = mode;
        }

        if (extraBody.Count > 0)
            payload["extra_body"] = extraBody;

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AgnesJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agnes video create failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var taskId = createRoot.TryGetString("id");

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Agnes video create response missing 'id'.");

        var terminal = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollAgnesVideoTaskAsync(taskId, ct),
            isTerminal: status => AgnesVideoStatusIsTerminal(status.Status),
            interval: TimeSpan.FromSeconds(Math.Max(1, ResolveAgnesPollIntervalSeconds(metadata))),
            timeout: TimeSpan.FromMinutes(Math.Max(1, ResolveAgnesPollTimeoutMinutes(metadata))),
            maxAttempts: ResolveAgnesPollMaxAttempts(metadata),
            cancellationToken: cancellationToken);

        if (!string.Equals(terminal.Status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Agnes video generation failed with status '{terminal.Status}': {GetAgnesVideoError(terminal.Root)}");

        var videoUrl = terminal.Root.TryGetString("video_url", "videoUrl");
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("Agnes video response missing 'video_url'.");

        var (bytes, mediaType) = await DownloadAgnesBinaryAsync(
            videoUrl,
            GuessAgnesVideoMediaType(videoUrl) ?? "video/mp4",
            cancellationToken);

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
            ProviderMetadata = GetIdentifier()
            .CreatePrimitiveProviderMetadata(new
            {
                create = createRoot,
                retrieve = terminal.Root
            }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<AgnesVideoTaskStatus> PollAgnesVideoTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{taskId}");
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agnes video poll failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = root.TryGetString("status") ?? "queued";
        return new AgnesVideoTaskStatus(status, root);
    }
}
