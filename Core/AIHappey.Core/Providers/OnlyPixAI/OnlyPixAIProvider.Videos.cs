using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OnlyPixAI;

public partial class OnlyPixAIProvider
{
    private const string OnlyPixAIVideoOperationTokenPrefix = "opxv1_";

    private static readonly JsonSerializerOptions PixCodeVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var providerOptions = GetPixCodeVideoProviderOptions(request);
        ValidatePixCodeVideoRequest(request, providerOptions);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var payload = BuildPixCodeVideoPayload(request, providerOptions);
        var json = JsonSerializer.Serialize(payload, PixCodeVideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/video/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PixCode video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var taskId = TryGetString(createDoc.RootElement, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("PixCode video generation returned no task_id.");

        var providerMetadata = GetIdentifier()
            .CreatePrimitiveProviderMetadata(new
            {
                family = "video-task",
                taskId,
                status = TryGetString(createDoc.RootElement, "status") ?? "QUEUED",
                create = createDoc.RootElement.Clone()
            });

        return new VideoOperationStartResult
        {
            Operation = EncodeOnlyPixAIVideoOperation(taskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeOnlyPixAIVideoOperation(operation);
        ApplyAuthHeader();

        using var pollReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/video/generations/{Uri.EscapeDataString(operationData.TaskId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PixCode video status failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = TryGetString(root, "status") ?? "UNKNOWN";
        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            family = "video-task",
            taskId = operationData.TaskId,
            status,
            poll = root
        });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (IsFailedVideoStatus(status))
        {
            return new VideoOperationErrorResult
            {
                Error = $"PixCode video generation failed with status '{status}' (task_id={operationData.TaskId}). Response: {pollRaw}",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        if (!IsSuccessfulVideoStatus(status))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        var videos = await DownloadOnlyPixAIVideos(root, operationData.TaskId, cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = providerMetadata,
            Response = response
        };
    }

    private static string EncodeOnlyPixAIVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new OnlyPixAIVideoOperationData(taskId, model), PixCodeVideoJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return OnlyPixAIVideoOperationTokenPrefix + base64Url;
    }

    private static OnlyPixAIVideoOperationData DecodeOnlyPixAIVideoOperation(string operation)
    {
        if (!operation.StartsWith(OnlyPixAIVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The OnlyPixAI video operation token is invalid.", nameof(operation));

        var base64Url = operation[OnlyPixAIVideoOperationTokenPrefix.Length..];
        if (base64Url.Length == 0 || base64Url.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The OnlyPixAI video operation token is invalid.", nameof(operation));

        var base64 = base64Url.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding != 0)
            base64 = base64.PadRight(base64.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var data = JsonSerializer.Deserialize<OnlyPixAIVideoOperationData>(json, PixCodeVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The OnlyPixAI video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The OnlyPixAI video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The OnlyPixAI video operation token is invalid.", nameof(operation), ex);
        }
    }

    private async Task<IReadOnlyList<VideoOperationVideoData>> DownloadOnlyPixAIVideos(
        JsonElement root,
        string taskId,
        CancellationToken cancellationToken)
    {
        var videoEntries = GetVideoEntries(root).ToList();
        if (videoEntries.Count == 0)
            throw new InvalidOperationException($"PixCode video task completed but returned no video url (task_id={taskId}).");

        List<VideoOperationVideoData> results = [];
        foreach (var (url, declaredType) in videoEntries)
        {
            using var videoResp = await _client.GetAsync(url, cancellationToken);
            var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!videoResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"PixCode video download failed ({(int)videoResp.StatusCode}, task_id={taskId}).");

            results.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = videoResp.Content.Headers.ContentType?.MediaType
                    ?? NormalizeVideoMediaType(declaredType)
                    ?? NormalizeVideoMediaType(url)
                    ?? "video/mp4",
                Data = Convert.ToBase64String(videoBytes)
            });
        }

        return results;
    }

    private static IEnumerable<(string Url, string? DeclaredType)> GetVideoEntries(JsonElement root)
    {
        if (TryGetString(root, "video_url") is { } directUrl && !string.IsNullOrWhiteSpace(directUrl))
            yield return (directUrl, TryGetString(root, "video_type"));

        if (!root.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var video in videos.EnumerateArray())
        {
            var url = TryGetString(video, "video_url") ?? TryGetString(video, "url");
            if (!string.IsNullOrWhiteSpace(url))
                yield return (url, TryGetString(video, "video_type"));
        }
    }

    private sealed record OnlyPixAIVideoOperationData(string TaskId, string Model);

    private Dictionary<string, object?> BuildPixCodeVideoPayload(VideoRequest request, JsonElement? providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model
        };

        var input = new Dictionary<string, object?>();
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            input["prompt"] = request.Prompt;

        if (request.Image is not null && !string.IsNullOrWhiteSpace(request.Image.Data))
            input["img_url"] = request.Image.Data;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            parameters["size"] = request.Resolution;

        if (request.Duration is not null)
            parameters["duration"] = request.Duration;

        if (request.Seed is not null)
            parameters["seed"] = request.Seed;

        if (providerOptions.HasValue && providerOptions.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.Value.EnumerateObject())
            {
                if (property.NameEquals("model"))
                    continue;

                if (property.NameEquals("input") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    MergeObjectValues(input, property.Value);
                    continue;
                }

                if (property.NameEquals("parameters") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    MergeObjectValues(parameters, property.Value);
                    continue;
                }

                payload[property.Name] = property.Value.Clone();
            }
        }

        if (input.Count > 0)
            payload["input"] = input;

        if (parameters.Count > 0)
            payload["parameters"] = parameters;

        return payload;
    }

    private static void MergeObjectValues(Dictionary<string, object?> target, JsonElement source)
    {
        foreach (var property in source.EnumerateObject())
            target[property.Name] = property.Value.Clone();
    }

    private static JsonElement? GetPixCodeVideoProviderOptions(VideoRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue(nameof(OnlyPixAI).ToLowerInvariant(), out var options))
            return null;

        return options.ValueKind == JsonValueKind.Object
            ? options.Clone()
            : null;
    }

    private static void ValidatePixCodeVideoRequest(VideoRequest request, JsonElement? providerOptions)
    {
        var hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt) || HasProviderInputValue(providerOptions, "prompt");
        var hasImage = request.Image is not null || HasProviderInputValue(providerOptions, "img_url");
        var hasReferenceUrls = HasProviderInputValue(providerOptions, "reference_urls");

        if (request.Model.Contains("-t2v", StringComparison.OrdinalIgnoreCase) && !hasPrompt)
            throw new ArgumentException("Prompt is required for PixCode text-to-video models.", nameof(request));

        if (request.Model.Contains("-i2v", StringComparison.OrdinalIgnoreCase) && !hasImage)
            throw new ArgumentException("Image is required for PixCode image-to-video models.", nameof(request));

        if (request.Model.Contains("-v2v", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasPrompt)
                throw new ArgumentException("Prompt is required for PixCode reference-video models.", nameof(request));

            if (!hasReferenceUrls)
                throw new ArgumentException("reference_urls is required for PixCode reference-video models.", nameof(request));
        }

        if (!hasPrompt && !hasImage && !hasReferenceUrls)
            throw new ArgumentException("Prompt, image, or provider video input is required.", nameof(request));
    }

    private static bool HasProviderInputValue(JsonElement? providerOptions, string propertyName)
    {
        if (!providerOptions.HasValue || providerOptions.Value.ValueKind != JsonValueKind.Object)
            return false;

        if (!providerOptions.Value.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return false;

        if (!input.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => true
        };
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static string? NormalizeVideoMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return value;

        if (value.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || value.Equals("webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (value.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        if (value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static bool IsFailedVideoStatus(string status)
        => status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
           || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)
           || status.Equals("ERROR", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessfulVideoStatus(string status)
        => status.Equals("SUCCEED", StringComparison.OrdinalIgnoreCase);
}
