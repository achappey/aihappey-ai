using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.IonRouter;

public partial class IonRouterProvider
{
    private const string IonRouterVideoOperationTokenPrefix = "ionv1_";
    private static readonly JsonSerializerOptions IonRouterVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.GenerateAudio is not null)
            warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        if (request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "inputReferences" });

        var payload = BuildIonRouterVideoPayload(request);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/video/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, IonRouterVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"IonRouter video generation failed ({(int)createResponse.StatusCode})."
                : $"IonRouter video generation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var jobId = ReadIonRouterString(root, "id", "job_id", "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("IonRouter video generation returned no job id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeIonRouterVideoOperation(jobId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeIonRouterVideoOperation(operation);
        ApplyAuthHeader();
        using var pollRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/video/generations/{Uri.EscapeDataString(operationData.JobId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var raw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"IonRouter video poll failed ({(int)pollResponse.StatusCode})."
                : $"IonRouter video poll failed ({(int)pollResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var status = ReadIonRouterString(root, "status") ?? "pending";
        var model = ReadIonRouterString(root, "model") ?? operationData.Model;
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = pollResponse.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone());

        if (IsIonRouterFailedStatus(status))
            return new VideoOperationErrorResult
            {
                Error = ReadIonRouterError(root) ?? $"IonRouter video generation failed (job_id={operationData.JobId}).",
                ProviderMetadata = metadata,
                Response = response
            };

        if (!IsIonRouterSucceededStatus(status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var videoUrl = ReadIonRouterVideoUrl(root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult
            {
                Error = $"IonRouter job '{operationData.JobId}' succeeded but returned no video URL.",
                ProviderMetadata = metadata,
                Response = response
            };

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "url",
                MediaType = GuessVideoMediaType(videoUrl) ?? "video/mp4",
                Data = videoUrl
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private Dictionary<string, object?> BuildIonRouterVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.Image is not null)
            payload["image_url"] = NormalizeIonRouterVideoImage(request.Image);

        var size = ParseIonRouterSize(request.Resolution);
        if (size is null && !string.IsNullOrWhiteSpace(request.AspectRatio))
            size = request.AspectRatio.InferSizeFromAspectRatio();
        if (size is not null)
        {
            payload["width"] = size.Value.width;
            payload["height"] = size.Value.height;
        }

        return payload;
    }

    private static string NormalizeIonRouterVideoImage(VideoFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Video image data is required.", nameof(image));
        if (image.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return image.Data;
        if (string.IsNullOrWhiteSpace(image.MediaType))
            throw new ArgumentException("Video image media type is required for base64 data.", nameof(image));
        return image.Data.ToDataUrl(image.MediaType);
    }

    private static string EncodeIonRouterVideoOperation(string jobId, string model)
    {
        var json = JsonSerializer.Serialize(new IonRouterVideoOperationData(jobId, model), IonRouterVideoJsonOptions);
        var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return IonRouterVideoOperationTokenPrefix + value;
    }

    private static IonRouterVideoOperationData DecodeIonRouterVideoOperation(string operation)
    {
        if (!operation.StartsWith(IonRouterVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new IonRouterVideoOperationData(Uri.UnescapeDataString(operation), null);

        var value = operation[IonRouterVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (value.Length % 4 != 0)
            value = value.PadRight(value.Length + (4 - value.Length % 4), '=');
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var data = JsonSerializer.Deserialize<IonRouterVideoOperationData>(json, IonRouterVideoJsonOptions);
            return data is null || string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model)
                ? throw new ArgumentException("The IonRouter video operation token is invalid.", nameof(operation))
                : data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The IonRouter video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ReadIonRouterVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
            return ReadIonRouterString(output, "video_url", "videoUrl", "url");
        return ReadIonRouterString(root, "video_url", "videoUrl", "url");
    }

    private static string? ReadIonRouterError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
            return null;
        return error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : ReadIonRouterString(error, "message", "detail") ?? error.GetRawText();
    }

    private static string? ReadIonRouterString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        return null;
    }

    private static bool IsIonRouterSucceededStatus(string status)
        => status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsIonRouterFailedStatus(string status)
        => status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

    private static string? GuessVideoMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".mp4" => "video/mp4",
            _ => null
        };
    }

    private sealed record IonRouterVideoOperationData(string JobId, string? Model);
}
