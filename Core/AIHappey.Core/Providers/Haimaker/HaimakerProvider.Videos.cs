using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Haimaker;

public partial class HaimakerProvider
{
    private static readonly JsonSerializerOptions HaimakerVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record HaimakerVideoStatus(string Status, JsonElement Root);

    private async Task<VideoResponse> VideoRequestInternal(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        JsonElement createRoot;

        if (request.Image is null)
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["seconds"] = request.Duration?.ToString(CultureInfo.InvariantCulture),
                ["size"] = request.Resolution,
                ["seed"] = request.Seed
            };

            var createJson = JsonSerializer.Serialize(payload, HaimakerVideoJson);
            using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
            {
                Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            using var createResp = await _client.SendAsync(createReq, cancellationToken);
            var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

            if (!createResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Haimaker video create failed ({(int)createResp.StatusCode}): {createRaw}");

            using var createDoc = JsonDocument.Parse(createRaw);
            createRoot = createDoc.RootElement.Clone();
        }
        else
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent(request.Prompt), "prompt");

            if (request.Duration is not null)
            {
                form.Add(
                    new StringContent(request.Duration.Value.ToString(CultureInfo.InvariantCulture)),
                    "seconds");
            }

            if (!string.IsNullOrWhiteSpace(request.Resolution))
                form.Add(new StringContent(request.Resolution), "size");

            if (request.Seed is not null)
                form.Add(new StringContent(request.Seed.Value.ToString(CultureInfo.InvariantCulture)), "seed");

            var imageContent = CreateVideoReferenceContent(request.Image);
            form.Add(imageContent, "input_reference", "input_reference");

            using var createResp = await _client.PostAsync("v1/videos", form, cancellationToken);
            var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

            if (!createResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Haimaker video create failed ({(int)createResp.StatusCode}): {createRaw}");

            using var createDoc = JsonDocument.Parse(createRaw);
            createRoot = createDoc.RootElement.Clone();
        }

        var videoId = createRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("Haimaker video create did not return an id.");

        var terminal = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollVideoAsync(videoId, ct),
            isTerminal: s => IsTerminalVideoStatus(s.Status),
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(terminal.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var err = TryGetVideoError(terminal.Root);
            throw new InvalidOperationException($"Haimaker video generation failed with status '{terminal.Status}': {err}");
        }

        using var contentResp = await _client.GetAsync($"v1/videos/{videoId}/content", cancellationToken);
        var bytes = await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Haimaker video download failed ({(int)contentResp.StatusCode}): {text}");
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
                    id = videoId,
                    status = terminal.Status,
                    progress = TryGetProgress(terminal.Root),
                    create = createRoot,
                    final = terminal.Root
                }, HaimakerVideoJson)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = terminal.Root.Clone()
            }
        };
    }

    private async Task<HaimakerVideoStatus> PollVideoAsync(string videoId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{videoId}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Haimaker video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "queued"
            : "queued";

        return new HaimakerVideoStatus(status, root);
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

    private static ByteArrayContent CreateVideoReferenceContent(VideoFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Image data is required.", nameof(image));

        var bytes = Convert.FromBase64String(image.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(image.MediaType)
                ? MediaTypeNames.Application.Octet
                : image.MediaType);

        return content;
    }
}
