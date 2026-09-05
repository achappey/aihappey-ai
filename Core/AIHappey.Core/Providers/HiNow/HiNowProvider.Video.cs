using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HiNow;

public partial class HiNowProvider
{
    private const string HiNowVideoTokenPrefix = "hnv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = GetHiNowOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["async"] = true;
        SetHiNow(payload, "duration", request.Duration);
        var images = GetHiNowVideoImages(request);
        if (images.Count > 0) payload["images"] = JsonSerializer.SerializeToNode(images, HiNowJson);
        var result = await SendHiNowJsonAsync(HttpMethod.Post, "v1/videos", payload, "video generation", cancellationToken);
        var data = GetHiNowData(result.Root);
        var jobId = GetHiNowString(data, "job_id", "id")
            ?? throw new InvalidOperationException("HiNow video response did not contain a job_id.");
        return new VideoOperationStartResult
        {
            Operation = EncodeHiNowVideoToken(jobId, request.Model), Warnings = BuildHiNowVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var token = DecodeHiNowVideoToken(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/media/jobs/{Uri.EscapeDataString(token.JobId)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HiNow video status failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var data = GetHiNowData(root);
        var status = GetHiNowString(data, "status")?.ToLowerInvariant() ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(),
            // The submitted model carried by the opaque token is authoritative for every status response.
            ModelId = token.Model.ToModelId(GetIdentifier())
        };
        if (status is "queued" or "running" or "pending" or "processing")
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = responseData };
        if (status is not "succeeded" and not "success" and not "completed")
            return new VideoOperationErrorResult
            {
                Error = ExtractHiNowJobError(data) ?? $"HiNow video job '{token.JobId}' failed with status '{status}'.",
                ProviderMetadata = metadata, Response = responseData
            };
        var resultData = data.TryGetProperty("result", out var result) ? result : data;
        var urls = GetHiNowUrls(resultData);
        if (urls.Count == 0) return new VideoOperationErrorResult
        { Error = $"HiNow video job '{token.JobId}' completed without a video URL.", ProviderMetadata = metadata, Response = responseData };
        var videos = new List<VideoOperationVideoData>();
        foreach (var url in urls)
        {
            var media = await DownloadHiNowMediaAsync(url, "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(media.Bytes), MediaType = media.MediaType });
        }
        return new VideoOperationCompletedResult { Videos = videos, Warnings = [], ProviderMetadata = metadata, Response = responseData };
    }

    private static List<string> GetHiNowVideoImages(VideoRequest request)
    {
        var frames = request.FrameImages?.Where(x => x?.Image is not null).ToList() ?? [];
        if (frames.Count > 0)
        {
            var ordered = frames.OrderBy(x => x.FrameType.Contains("last", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            return ordered.Select(x => ToHiNowVideoImageValue(x.Image)).Take(2).ToList();
        }
        var images = new List<string>();
        if (request.Image is not null) images.Add(ToHiNowVideoImageValue(request.Image));
        images.AddRange(request.InputReferences?.Where(x => x is not null).Select(ToHiNowVideoImageValue) ?? []);
        return images;
    }

    private static string ToHiNowVideoImageValue(VideoFile image)
        => image.Type.Equals("url", StringComparison.OrdinalIgnoreCase) || image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? image.Data : image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? image.Data : $"data:{image.MediaType};base64,{image.Data}";

    private static List<object> BuildHiNowVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        if (!string.IsNullOrWhiteSpace(request.Resolution)) warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        return warnings;
    }

    private static string EncodeHiNowVideoToken(string jobId, string model)
        => HiNowVideoTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { jobId, model }, HiNowJson)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HiNowVideoToken DecodeHiNowVideoToken(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(HiNowVideoTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware HiNow video operation token is required.", nameof(operation));
        try
        {
            var encoded = operation[HiNowVideoTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var token = JsonSerializer.Deserialize<HiNowVideoToken>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), HiNowJson);
            if (token is null || string.IsNullOrWhiteSpace(token.JobId) || string.IsNullOrWhiteSpace(token.Model)) throw new JsonException();
            return token;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new ArgumentException("The HiNow video operation token is invalid.", nameof(operation), exception); }
    }

    private static string? ExtractHiNowJobError(JsonElement data)
    {
        var direct = GetHiNowString(data, "message", "error_message", "detail");
        if (direct is not null) return direct;
        return data.TryGetProperty("error", out var error) ? GetHiNowString(error, "message", "details", "code") ?? error.ToString() : null;
    }

    private sealed record HiNowVideoToken(string JobId, string Model);
}
