using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MegaNova;

public partial class MegaNovaProvider
{
    private static readonly JsonSerializerOptions MegaNovaVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record MegaNovaVideoPollResult(string Status, string Raw, JsonElement Root);

    private async Task<VideoResponse> VideoRequestMegaNova(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetMegaNovaProviderMetadata(request, GetIdentifier());
        var payload = BuildMegaNovaVideoPayload(request, metadata, warnings);
        var createJson = JsonSerializer.Serialize(payload, MegaNovaVideoJsonOptions);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generations")
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"MegaNova video generation failed ({(int)createResponse.StatusCode})."
                : $"MegaNova video generation failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var generationId = TryGetMegaNovaVideoId(createRoot)
            ?? throw new InvalidOperationException("MegaNova video generation returned no id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollMegaNovaVideoAsync(generationId, ct),
            isTerminal: result => IsMegaNovaVideoTerminal(result.Status),
            interval: TimeSpan.FromSeconds(10),
            timeout: TimeSpan.FromMinutes(15),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (IsMegaNovaVideoFailed(completed.Status))
        {
            var error = TryGetMegaNovaString(completed.Root, "error", "message", "failure_reason", "failureReason") ?? completed.Raw;
            throw new InvalidOperationException($"MegaNova video generation failed with status '{completed.Status}': {error}");
        }

        using var streamResponse = await _client.GetAsync(
            $"v1/videos/generations/{Uri.EscapeDataString(generationId)}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var videoBytes = await streamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!streamResponse.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(videoBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"MegaNova video download failed ({(int)streamResponse.StatusCode})."
                : $"MegaNova video download failed ({(int)streamResponse.StatusCode}): {error}");
        }

        var contentType = streamResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4";
        var providerMeta = new
        {
            create = createRoot,
            poll = completed.Root
        };

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = contentType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(providerMeta),
            Response = new HeaderResponseData
            {
                Timestamp = now,
                Headers = streamResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<MegaNovaVideoPollResult> PollMegaNovaVideoAsync(string generationId, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"v1/videos/generations/{Uri.EscapeDataString(generationId)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MegaNova video status failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var status = TryGetMegaNovaString(root, "status", "state") ?? "unknown";
        return new MegaNovaVideoPollResult(status, raw, root);
    }

    private static Dictionary<string, object?> BuildMegaNovaVideoPayload(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        MergeMegaNovaProviderMetadata(payload, metadata);

        payload["model"] = request.Model.Trim();
        payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (request.Duration is not null)
            payload["duration"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.Fps is not null)
            payload["fps"] = request.Fps;
        if (request.Seed is not null)
            payload["seed"] = request.Seed;
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Image is not null)
            payload["image"] = NormalizeMegaNovaVideoImage(request.Image);

        if (request.InputReferences?.Any() == true)
            payload["input_references"] = request.InputReferences.Select(NormalizeMegaNovaVideoImage).ToArray();

        if (request.FrameImages?.Any() == true)
            payload["frame_images"] = request.FrameImages.Select(frame => new Dictionary<string, object?>
            {
                ["frame_type"] = frame.FrameType,
                ["image"] = NormalizeMegaNovaVideoImage(frame.Image)
            }).ToArray();

        return payload;
    }

    private static string NormalizeMegaNovaVideoImage(VideoFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        return file.Data;
    }

    private static string? TryGetMegaNovaVideoId(JsonElement root)
        => TryGetMegaNovaString(root, "id", "generation_id", "generationId", "task_id", "taskId");

    private static bool IsMegaNovaVideoTerminal(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMegaNovaVideoFailed(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return true;

        return status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }
}
