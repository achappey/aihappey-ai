using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.MiniMax;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider : IModelProvider
{
    private static readonly JsonSerializerOptions VideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MiniMax video generation only supports base64 or data URLs for images.");

        var metadata = GetVideoProviderMetadata<MiniMaxVideoProviderMetadata>(request, GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelName(request.Model)
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (request.Image is not null)
        {
            var imageData = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? request.Image.Data
                : request.Image.Data.ToDataUrl(request.Image.MediaType);

            payload["first_frame_image"] = imageData;
        }

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (metadata?.PromptOptimizer is not null)
            payload["prompt_optimizer"] = metadata.PromptOptimizer;

        if (metadata?.FastPretreatment is not null)
            payload["fast_pretreatment"] = metadata.FastPretreatment;

        var json = JsonSerializer.Serialize(payload, VideoJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/video_generation")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"MiniMax video_generation failed ({(int)createResp.StatusCode})"
                : $"MiniMax video_generation failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);

        EnsureBaseResponseOk(createDoc.RootElement, "video_generation");

        var taskId = createDoc.RootElement.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("MiniMax video generation returned no task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/query/video_generation?task_id={taskId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);

                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"MiniMax video_generation poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return (root: pollDoc.RootElement.Clone(), raw: pollRaw);
            },
            result =>
            {
                var status = TryGetStatus(result.root);
                return string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Fail", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(5),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        EnsureBaseResponseOk(completed.root, "video_generation_query");

        var finalStatus = TryGetStatus(completed.root);
        if (!string.Equals(finalStatus, "Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MiniMax video generation failed with status '{finalStatus}'.");

        var fileId = completed.root.TryGetProperty("file_id", out var fileEl) ? fileEl.ToString() : null;
        if (string.IsNullOrWhiteSpace(fileId))
            throw new InvalidOperationException("MiniMax video generation result contained no file_id.");

        var downloadUrl = await ResolveDownloadUrlAsync(fileId, cancellationToken);
        var videoBytes = await _client.GetByteArrayAsync(downloadUrl, cancellationToken);
        var mediaType = GuessVideoMediaType(downloadUrl) ?? "video/mp4";

        Dictionary<string, JsonElement>? providerMetadata = null;
        try
        {
            var meta = new Dictionary<string, JsonElement>
            {
                ["create"] = createDoc.RootElement.Clone(),
                ["query"] = completed.root.Clone()
            };

            providerMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(meta, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            // best-effort only
        }

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = createDoc.RootElement.Clone()
            }
        };
    }

    private static string? TryGetStatus(JsonElement root)
    {
        return root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;
    }

    private async Task<string> ResolveDownloadUrlAsync(string fileId, CancellationToken cancellationToken)
    {
        using var retrieveReq = new HttpRequestMessage(HttpMethod.Get, $"v1/files/retrieve?file_id={fileId}");
        using var retrieveResp = await _client.SendAsync(retrieveReq, cancellationToken);
        var retrieveRaw = await retrieveResp.Content.ReadAsStringAsync(cancellationToken);

        if (!retrieveResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax file retrieve failed ({(int)retrieveResp.StatusCode}): {retrieveRaw}");

        using var retrieveDoc = JsonDocument.Parse(retrieveRaw);
        EnsureBaseResponseOk(retrieveDoc.RootElement, "file_retrieve");

        var fileObj = retrieveDoc.RootElement.TryGetProperty("file", out var fileEl) ? fileEl : default;
        var downloadUrl = fileObj.ValueKind == JsonValueKind.Object
            && fileObj.TryGetProperty("download_url", out var urlEl)
            && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("MiniMax retrieve file response contained no download_url.");

        return downloadUrl;
    }

    private static void EnsureBaseResponseOk(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("base_resp", out var baseResp) || baseResp.ValueKind != JsonValueKind.Object)
            return;

        if (!baseResp.TryGetProperty("status_code", out var statusCodeEl) || statusCodeEl.ValueKind != JsonValueKind.Number)
            return;

        var statusCode = statusCodeEl.GetInt32();
        if (statusCode == 0)
            return;

        var statusMsg = baseResp.TryGetProperty("status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString()
            : "MiniMax request failed";

        throw new InvalidOperationException($"MiniMax {operation} failed (status_code={statusCode}, status_msg={statusMsg}).");
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}
