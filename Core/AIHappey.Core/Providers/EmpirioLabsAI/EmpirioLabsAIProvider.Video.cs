using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    private const string EmpirioVideoOperationPrefix = "elv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        var payload = CreateEmpirioVercelPayload(request.ProviderOptions,
            "model", "prompt", "resolution", "aspect_ratio", "aspectRatio", "duration", "generate_audio", "generateAudio",
            "image", "image_url", "images", "reference_images", "inputReferences", "frameImages");
        payload["model"] = request.Model;
        SetEmpirio(payload, "prompt", request.Prompt);
        SetEmpirio(payload, "resolution", request.Resolution);
        SetEmpirio(payload, "aspect_ratio", request.AspectRatio);
        SetEmpirio(payload, "duration", request.Duration);
        SetEmpirio(payload, "generate_audio", request.GenerateAudio);

        var primary = request.Image ?? request.FrameImages?.FirstOrDefault(frame =>
            string.Equals(frame.FrameType, "first_frame", StringComparison.OrdinalIgnoreCase))?.Image;
        if (primary is not null) payload["image"] = EmpirioVideoFileValue(primary);
        var references = new JsonArray();
        foreach (var reference in request.InputReferences ?? []) references.Add(EmpirioVideoFileValue(reference));
        if (references.Count > 0) payload["reference_images"] = references;

        var result = await SendEmpirioJsonAsync(HttpMethod.Post, "v1/videos/generations", payload, "video creation", cancellationToken);
        var jobId = GetEmpirioString(result.Root, "job_id")
            ?? throw new InvalidOperationException("EmpirioLabs video creation did not return a job_id.");
        var pollUrl = GetEmpirioString(result.Root, "poll_url");
        return new VideoOperationStartResult
        {
            Operation = EncodeEmpirioVideoOperation(jobId, request.Model, pollUrl),
            Warnings = BuildEmpirioVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeEmpirioVideoOperation(operation);
        var endpoint = NormalizeEmpirioPollUrl(operationData.PollUrl, operationData.JobId);
        var result = await SendEmpirioJsonAsync(HttpMethod.Get, endpoint, null, "video status", cancellationToken);
        var status = GetEmpirioString(result.Root, "status");
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = result.Headers,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult
            {
                Error = GetEmpirioError(result.Root),
                ProviderMetadata = metadata,
                Response = response
            };

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };

        var urls = new List<string>();
        CollectEmpirioVideoUrls(result.Root, urls);
        if (urls.Count == 0)
            throw new InvalidOperationException($"EmpirioLabs completed video job contained no video URL: {result.Root.GetRawText()}");
        var videos = new List<VideoOperationVideoData>();
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            var media = await DownloadEmpirioMediaAsync(url, "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(media.Bytes),
                MediaType = media.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? media.MediaType : "video/mp4"
            });
        }
        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeEmpirioVideoOperation(string jobId, string model, string? pollUrl)
    {
        var json = JsonSerializer.Serialize(new EmpirioVideoOperation(jobId, model, pollUrl), EmpirioMediaJson);
        return EmpirioVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static EmpirioVideoOperation DecodeEmpirioVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(EmpirioVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware EmpirioLabs video operation token is required.", nameof(operation));
        try
        {
            var encoded = operation[EmpirioVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var data = JsonSerializer.Deserialize<EmpirioVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), EmpirioMediaJson);
            if (data is null || string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The EmpirioLabs video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The EmpirioLabs video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string NormalizeEmpirioPollUrl(string? pollUrl, string jobId)
    {
        if (string.IsNullOrWhiteSpace(pollUrl)) return $"v1/jobs/{Uri.EscapeDataString(jobId)}";
        if (Uri.TryCreate(pollUrl, UriKind.Absolute, out var absolute))
        {
            if (!string.Equals(absolute.Host, "api.empiriolabs.ai", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The EmpirioLabs poll URL host is invalid.", nameof(pollUrl));
            return absolute.PathAndQuery.TrimStart('/');
        }
        return pollUrl.TrimStart('/');
    }

    private static void CollectEmpirioVideoUrls(JsonElement element, List<string> urls, string? propertyName = null)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value)
                && (propertyName?.Contains("video", StringComparison.OrdinalIgnoreCase) == true
                    || propertyName is "url")
                && (value.StartsWith("http", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:video", StringComparison.OrdinalIgnoreCase)))
                urls.Add(value);
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) CollectEmpirioVideoUrls(property.Value, urls, property.Name);
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectEmpirioVideoUrls(item, urls, propertyName);
    }

    private static IEnumerable<object> BuildEmpirioVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.FrameImages?.Any(frame => string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)) == true)
            warnings.Add(new { type = "unsupported", feature = "lastFrame" });
        return warnings;
    }

    private static string EmpirioVideoFileValue(VideoFile file)
        => file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data : $"data:{file.MediaType};base64,{file.Data}";

    private sealed record EmpirioVideoOperation(string JobId, string Model, string? PollUrl);
}
