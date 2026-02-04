using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Vidu;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{
    private static readonly JsonSerializerOptions ViduImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ViduImageCreationResult(string State, JsonElement RawRoot);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        var model = request.Model.Trim();
        var isViduQ2 = string.Equals(model, "viduq2", StringComparison.OrdinalIgnoreCase);
        var isViduQ1 = string.Equals(model, "viduq1", StringComparison.OrdinalIgnoreCase);

        if (!isViduQ1 && !isViduQ2)
            throw new NotSupportedException($"Vidu image model '{request.Model}' is not supported.");

        var images = BuildViduReferenceImages(request);
        if (images is { Count: > 7 })
            throw new ArgumentException("Vidu supports up to 7 reference images.", nameof(request));

        if (isViduQ1 && (images is null || images.Count == 0))
            throw new ArgumentException("Viduq1 requires at least one reference image.", nameof(request));

        var imageMetadata = request.GetProviderMetadata<ViduImageProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["seed"] = request.Seed,
            ["aspect_ratio"] = request.AspectRatio,
            ["resolution"] = imageMetadata?.Resolution,
            ["payload"] = imageMetadata?.Payload,
            ["callback_url"] = imageMetadata?.CallbackUrl
        };

        if (images is { Count: > 0 })
            payload["images"] = images;

        var json = JsonSerializer.Serialize(payload, ViduImageJsonOptions);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, "reference2image")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu image request failed ({(int)startResp.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Vidu response missing task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollImageCreationsAsync(taskId, ct),
            isTerminal: r => r.State is "success" or "failed",
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (completed.State == "failed")
            throw new InvalidOperationException($"Vidu image task failed (task_id={taskId}).");

        var creationUrl = ViduProvider.TryGetFirstCreationUrl(completed.RawRoot);
        if (string.IsNullOrWhiteSpace(creationUrl))
            throw new InvalidOperationException($"Vidu image task completed but returned no creation url (task_id={taskId}).");

        using var fileResp = await _client.GetAsync(creationUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Vidu image download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessImageMediaType(creationUrl)
            ?? MediaTypeNames.Image.Png;

        return new ImageResponse
        {
            Images = [Convert.ToBase64String(fileBytes).ToDataUrl(mediaType)],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.RawRoot.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = startDoc.RootElement.Clone()
            }
        };
    }

    private async Task<ViduImageCreationResult> PollImageCreationsAsync(string taskId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"tasks/{taskId}/creations");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu task poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var state = root.TryGetProperty("state", out var stateEl)
            ? stateEl.GetString() ?? "unknown"
            : "unknown";

        return new ViduImageCreationResult(state, root);
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        return null;
    }

    private static List<string>? BuildViduReferenceImages(ImageRequest request)
    {
        if (request.Files?.Any() != true)
            return null;

        return [
            .. request.Files
                .Select(f => f.Data.ToDataUrl(f.MediaType))
                .Where(s => !string.IsNullOrWhiteSpace(s))
        ];
    }
}

