using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Globalization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OrcaRouter;

public partial class OrcaRouterProvider
{
    private const string VideoGenerationsEndpoint = "v1/video/generations";
    private static readonly JsonSerializerOptions VideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var submittedAt = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildVideoPayload(request, metadata);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, VideoGenerationsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw CreateVideoException("submit", createResponse, createRaw);

        using var createDocument = JsonDocument.Parse(createRaw);
        var createResult = createDocument.RootElement.Clone();
        var taskId = GetString(createResult, "task_id") ?? GetString(createResult, "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("OrcaRouter video submission response did not contain a task id.");

        var terminal = await PollVideoAsync(taskId, cancellationToken);
        var state = GetVideoState(terminal);
        if (!IsSuccessfulVideoState(state))
            throw new InvalidOperationException($"OrcaRouter video generation failed with status '{state ?? "unknown"}' (task_id={taskId}): {GetString(GetVideoData(terminal), "fail_reason") ?? GetString(terminal, "message") ?? "No failure reason was returned."}");

        var resultUrl = GetString(GetVideoData(terminal), "result_url")
            ?? GetString(terminal, "url")
            ?? GetString(terminal, "video_url");
        if (string.IsNullOrWhiteSpace(resultUrl))
            throw new InvalidOperationException($"OrcaRouter video task completed without a result URL (task_id={taskId}).");

        var (video, mediaType) = await DownloadVideoAsync(resultUrl, cancellationToken);
        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    Data = Convert.ToBase64String(video),
                    MediaType = mediaType
                }
            ],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                submit = createResult,
                status = terminal
            }),
            Response = new()
            {
                Timestamp = submittedAt,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildVideoPayload(VideoRequest request, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (request.Image is not null)
            payload["image"] = ToVideoInput(request.Image);

        var providerMetadata = metadata.ValueKind == JsonValueKind.Object
            ? metadata.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        if (!providerMetadata.ContainsKey("metadata"))
        {
            var generatedMetadata = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                generatedMetadata["aspect_ratio"] = request.AspectRatio;
            if (request.Duration is not null)
                generatedMetadata["duration"] = request.Duration.Value.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(request.Resolution))
                generatedMetadata["resolution"] = request.Resolution;
            if (request.Seed is not null)
                generatedMetadata["seed"] = request.Seed;
            if (generatedMetadata.Count > 0)
                providerMetadata["metadata"] = generatedMetadata;
        }

        foreach (var (key, value) in providerMetadata)
        {
            if (!string.Equals(key, "model", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "prompt", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "image", StringComparison.OrdinalIgnoreCase))
            {
                payload[key] = value;
            }
        }

        return payload;
    }

    private async Task<JsonElement> PollVideoAsync(string taskId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{VideoGenerationsEndpoint}/{Uri.EscapeDataString(taskId)}");
            using var response = await _client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw CreateVideoException("status", response, raw);

            using var document = JsonDocument.Parse(raw);
            var result = document.RootElement.Clone();
            var state = GetVideoState(result);
            if (IsTerminalVideoState(state))
                return result;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out waiting for OrcaRouter video task '{taskId}'.");

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task<(byte[] Video, string MimeType)> DownloadVideoAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw CreateVideoException("download", response, error);
        }

        return (
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "video/mp4");
    }

    private static string ToVideoInput(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Video image input data is required.", nameof(file));
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return file.Data;
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? "image/png" : file.MediaType)};base64,{file.Data}";
    }

    private static JsonElement GetVideoData(JsonElement result)
        => result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : result;

    private static string? GetVideoState(JsonElement result)
        => GetString(result, "status") ?? GetString(GetVideoData(result), "status") ?? GetString(GetVideoData(result), "progress");

    private static bool IsTerminalVideoState(string? status)
        => status?.Trim().ToUpperInvariant() is "SUCCESS" or "SUCCEEDED" or "COMPLETED" or "FAILED" or "ERROR" or "CANCELED" or "CANCELLED";

    private static bool IsSuccessfulVideoState(string? status)
        => status?.Trim().ToUpperInvariant() is "SUCCESS" or "SUCCEEDED" or "COMPLETED";

    private static InvalidOperationException CreateVideoException(string operation, HttpResponseMessage response, string content)
        => new(string.IsNullOrWhiteSpace(content)
            ? $"OrcaRouter video {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
            : $"OrcaRouter video {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {content}");
} 
