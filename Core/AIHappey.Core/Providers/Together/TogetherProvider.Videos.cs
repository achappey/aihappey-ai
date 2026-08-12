using AIHappey.Common.Model.Providers.Together;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider
{
    private const string TogetherVideoOperationTokenPrefix = "tgv1_";

    private static readonly JsonSerializerOptions VideoJsonSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record TogetherVideoOperationData(string JobId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var warnings = GetTogetherVideoWarnings(request);
        var metadata = GetVideoProviderMetadata<TogetherVideoProviderMetadata>(request, GetIdentifier());
        var payload = BuildTogetherVideoPayload(request, metadata);
        var jsonBody = JsonSerializer.Serialize(payload, VideoJsonSettings);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v2/videos")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Together video request failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var jobId = TryGetString(root, "id");
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Together video request did not return an id.");

        var returnedModel = TryGetString(root, "model");
        var model = string.IsNullOrWhiteSpace(returnedModel) ? request.Model : returnedModel;
        var status = TryGetString(root, "status") ?? "in_progress";

        return new VideoOperationStartResult
        {
            Operation = EncodeTogetherVideoOperation(jobId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { jobId, status, job = root }),
            Response = new()
            {
                Timestamp = ResolveTogetherVideoTimestamp(root),
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeTogetherVideoOperation(operation);
        ApplyAuthHeader();

        using var pollRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v2/videos/{Uri.EscapeDataString(operationData.JobId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var raw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Together video poll failed ({(int)pollResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = TryGetString(root, "status") ?? string.Empty;
        var providerModel = TryGetString(root, "model");
        var model = string.IsNullOrWhiteSpace(providerModel) ? operationData.Model : providerModel;
        var response = new HeaderResponseData
        {
            Timestamp = ResolveTogetherVideoTimestamp(root),
            ModelId = string.IsNullOrWhiteSpace(model)
                ? GetIdentifier()
                : model.ToModelId(GetIdentifier())
        };

        decimal? cost = null;
        if (root.TryGetProperty("outputs", out var outputs)
            && outputs.ValueKind == JsonValueKind.Object
            && outputs.TryGetProperty("cost", out var costElement)
            && costElement.ValueKind == JsonValueKind.Number
            && costElement.TryGetDecimal(out var parsedCost))
        {
            cost = parsedCost;
        }

        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(
            data: new { jobId = operationData.JobId, status, job = root },
            costs: cost);

        if (string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = GetTogetherVideoError(root, operationData.JobId),
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Together video job '{operationData.JobId}' returned unknown status '{status}'.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videoUrl = outputs.ValueKind == JsonValueKind.Object
            ? TryGetString(outputs, "video_url")
            : null;
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Together video job '{operationData.JobId}' completed but returned no video_url.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        using var videoResponse = await _client.GetAsync(videoUrl, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Together video download failed ({(int)videoResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = videoResponse.Content.Headers.ContentType?.MediaType
                        ?? ResolveVideoMediaType(null, videoUrl),
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildTogetherVideoPayload(
        VideoRequest request,
        TogetherVideoProviderMetadata? metadata)
    {
        var (width, height) = ParseResolution(request.Resolution);
        return new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["seconds"] = request.Duration?.ToString(),
            ["width"] = width,
            ["height"] = height,
            ["ratio"] = request.AspectRatio,
            ["fps"] = request.Fps,
            ["seed"] = request.Seed,
            ["generate_audio"] = request.GenerateAudio,
            ["steps"] = metadata?.Steps,
            ["guidance_scale"] = metadata?.GuidanceScale,
            ["output_format"] = metadata?.OutputFormat,
            ["output_quality"] = metadata?.OutputQuality,
            ["negative_prompt"] = metadata?.NegativePrompt
        };
    }

    private static List<object> GetTogetherVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "input_references" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frame_images" });
        return warnings;
    }

    private static string EncodeTogetherVideoOperation(string jobId, string model)
    {
        var json = JsonSerializer.Serialize(new TogetherVideoOperationData(jobId, model), VideoJsonSettings);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return TogetherVideoOperationTokenPrefix + base64Url;
    }

    private static TogetherVideoOperationData DecodeTogetherVideoOperation(string operation)
    {
        if (!operation.StartsWith(TogetherVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new TogetherVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[TogetherVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<TogetherVideoOperationData>(json, VideoJsonSettings);
            if (data is null || string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Together video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The Together video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTime ResolveTogetherVideoTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("created_at", out var createdAt)
            && createdAt.ValueKind == JsonValueKind.Number
            && createdAt.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static string GetTogetherVideoError(JsonElement root, string jobId)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = TryGetString(error, "message");
            var code = TryGetString(error, "code");
            if (!string.IsNullOrWhiteSpace(message))
                return string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}";
        }

        return $"Together video job '{jobId}' failed.";
    }

    private static (int? width, int? height) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return (null, null);
        var normalized = resolution.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return (null, null);
        return (
            int.TryParse(parts[0], out var width) ? width : null,
            int.TryParse(parts[1], out var height) ? height : null);
    }

    private static string ResolveVideoMediaType(string? outputFormat, string? videoUrl)
    {
        if (string.Equals(outputFormat, "webm", StringComparison.OrdinalIgnoreCase)
            || videoUrl?.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) == true)
            return "video/webm";
        return "video/mp4";
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(providerId, out var element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;
        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}
