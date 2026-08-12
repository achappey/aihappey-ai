using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
{
    private const string EverypixelVideoOperationPrefix = "epv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Duration is null) throw new ArgumentException("Duration is required by EverypixelLabs.", nameof(request));

        var warnings = new List<object>();
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeEverypixelModel(request.Model),
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration.Value
        };
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.GenerateAudio is not null) payload["generate_audio"] = request.GenerateAudio.Value;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;

        AddEverypixelVideoImages(payload, request);
        CopyEverypixelProviderOptions(request.ProviderOptions, payload, "callback_url", "lora_high_url", "lora_low_url", "reference_image_urls", "reference_video_urls");

        var rawBody = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/video_generate")
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} video generation failed ({(int)response.StatusCode}): {raw}");
        var create = DeserializeOrThrow<EverypixelTaskStatusResponse>(raw, "video create response");
        if (string.IsNullOrWhiteSpace(create.TaskId))
            throw new InvalidOperationException($"{ProviderName} video response missing task_id: {raw}");

        return new VideoOperationStartResult
        {
            Operation = EncodeEverypixelVideoOperation(create.TaskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { task_id = create.TaskId, status = create.Status, create = raw }),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A video operation is required.", nameof(operation));
        ApplyAuthHeader();
        var data = DecodeEverypixelVideoOperation(operation);
        var status = await GetTaskStatusAsync(data.TaskId, cancellationToken);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(data.Model) ? GetIdentifier() : data.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { task_id = data.TaskId, status = status.Status, poll = status.RawJson });

        if (string.Equals(status.Status, "FAILURE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.Status, "REVOKED", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = $"{ProviderName} video task failed (task_id={data.TaskId}, status={status.Status}).", ProviderMetadata = metadata, Response = response };

        if (!string.Equals(status.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var url = ExtractEverypixelResultUrls(status.Result, status.RawRoot).FirstOrDefault();
        if (url is null)
            return new VideoOperationErrorResult { Error = $"{ProviderName} video task succeeded but returned no video URL (task_id={data.TaskId}).", ProviderMetadata = metadata, Response = response };

        using var download = await _client.GetAsync(url, cancellationToken);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!download.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} video download failed ({(int)download.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        response.Headers = download.GetHeaders();

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = download.Content.Headers.ContentType?.MediaType ?? GuessEverypixelVideoMediaType(url),
                Data = Convert.ToBase64String(bytes)
            }],
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static void AddEverypixelVideoImages(Dictionary<string, object?> payload, VideoRequest request)
    {
        VideoFile? first = request.Image;
        VideoFile? last = null;
        foreach (var frame in request.FrameImages ?? [])
        {
            if (string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(frame.FrameType, "lastFrame", StringComparison.OrdinalIgnoreCase)) last = frame.Image;
            else if (string.Equals(frame.FrameType, "first_frame", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(frame.FrameType, "firstFrame", StringComparison.OrdinalIgnoreCase)) first = frame.Image;
            else throw new ArgumentException($"Unsupported frame type '{frame.FrameType}'.", nameof(request));
        }
        if (first is not null) payload["image_url"] = NormalizeEverypixelVideoFile(first);
        if (last is not null) payload["image_last_url"] = NormalizeEverypixelVideoFile(last);
        var references = request.InputReferences?.Select(NormalizeEverypixelVideoFile).ToArray();
        if (references?.Length > 0) payload["reference_image_urls"] = references;
    }

    private static string NormalizeEverypixelVideoFile(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data)) throw new ArgumentException("Video reference data is required.", nameof(file));
        var value = file.Data.Trim();
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
        if (string.IsNullOrWhiteSpace(file.MediaType)) throw new ArgumentException("Reference mediaType is required for base64 data.", nameof(file));
        return $"data:{file.MediaType};base64,{value}";
    }

    private static string EncodeEverypixelVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new EverypixelVideoOperationData(taskId, model), JsonSerializerOptions.Web);
        return EverypixelVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static EverypixelVideoOperationData DecodeEverypixelVideoOperation(string operation)
    {
        if (!operation.StartsWith(EverypixelVideoOperationPrefix, StringComparison.Ordinal))
            return new EverypixelVideoOperationData(Uri.UnescapeDataString(operation), null);
        var value = operation[EverypixelVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (value.Length % 4 != 0) value = value.PadRight(value.Length + 4 - value.Length % 4, '=');
        try
        {
            var data = JsonSerializer.Deserialize<EverypixelVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), JsonSerializerOptions.Web);
            return data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model)
                ? throw new ArgumentException("The EverypixelLabs video operation token is invalid.", nameof(operation))
                : data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The EverypixelLabs video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string GuessEverypixelVideoMediaType(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "video/mp4"
    };

    private sealed record EverypixelVideoOperationData(string TaskId, string? Model);
}
