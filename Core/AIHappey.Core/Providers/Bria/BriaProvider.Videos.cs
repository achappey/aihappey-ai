using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bria;

public partial class BriaProvider
{
    private const string BriaVideoOperationTokenPrefix = "brv1_";

    private static readonly IReadOnlyDictionary<string, string> BriaVideoEndpoints =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["video/edit/erase"] = "video/edit/erase",
            ["video/edit/increase_resolution"] = "video/edit/increase_resolution",
            ["video/edit/remove_background"] = "video/edit/remove_background",
            ["video/edit/replace_background"] = "video/edit/replace_background",
            ["video/edit/green_screen"] = "video/edit/green_screen",
            ["video/segment/mask_by_prompt"] = "video/segment/mask_by_prompt",
            ["video/segment/mask_by_key_points"] = "video/segment/mask_by_key_points",
            ["video/segment/foreground_mask"] = "video/segment/foreground_mask"
        };

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var model = NormalizeBriaVideoModel(request.Model);
        if (!BriaVideoEndpoints.TryGetValue(model, out var endpoint))
            throw new NotSupportedException($"Bria video model '{request.Model}' is not supported.");

        var warnings = new List<object>();
        var payload = BuildBriaVideoPayload(model, request, warnings);
        ApplyAuthHeader();

        using var response = await _client.PostAsync(
            endpoint,
            new StringContent(JsonSerializer.Serialize(payload, BriaJson), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bria video request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var requestId = TryGetString(root, "request_id")
            ?? throw new InvalidOperationException("Bria video response did not contain request_id.");
        var statusUrl = TryGetString(root, "status_url");

        return new VideoOperationStartResult
        {
            Operation = EncodeBriaVideoOperation(new(requestId, statusUrl, model)),
            Warnings = warnings,
            ProviderMetadata = ToProviderMetadata(new
            {
                requestId,
                statusUrl,
                status = TryGetString(root, "status") ?? "IN_PROGRESS"
            }),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
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

        var operationData = DecodeBriaVideoOperation(operation);
        ApplyAuthHeader();

        var pollUrl = ResolveBriaPollUrl(operationData);
        using var responseMessage = await _client.GetAsync(pollUrl, cancellationToken);
        var raw = await responseMessage.Content.ReadAsStringAsync(cancellationToken);
        if (!responseMessage.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bria video status request failed ({(int)responseMessage.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = TryGetString(root, "status") ?? "IN_PROGRESS";
        var requestId = TryGetString(root, "request_id") ?? operationData.RequestId;
        var metadata = ToProviderMetadata(new { requestId, status, job = root });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };

        if (status.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || status.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            || status.Equals("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = BuildBriaVideoError(root, status, requestId),
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var outputUrl = FindBriaOutputUrl(root);
        if (string.IsNullOrWhiteSpace(outputUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Bria video request '{requestId}' completed but returned no video or mask URL.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        using var outputClient = new HttpClient();
        using var outputResponse = await outputClient.GetAsync(outputUrl, cancellationToken);
        var bytes = await outputResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!outputResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Bria video output download failed ({(int)outputResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(bytes),
                    MediaType = outputResponse.Content.Headers.ContentType?.MediaType
                        ?? GuessBriaVideoMediaType(outputUrl)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildBriaVideoPayload(
        string model,
        VideoRequest request,
        List<object> warnings)
    {
        var payload = GetBriaVideoPassthroughOptions(request);
        var references = request.InputReferences?.Where(static file => file is not null).ToList() ?? [];

        VideoFile? primaryVideo = null;
        if (IsMediaType(request.Image, "video/"))
            primaryVideo = request.Image;
        else
        {
            primaryVideo = references.FirstOrDefault(static file => IsMediaType(file, "video/"));
            if (primaryVideo is not null)
                references.Remove(primaryVideo);
        }

        if (primaryVideo is not null)
            payload["video"] = ToBriaMediaValue(primaryVideo);
        else if (!HasUsableString(payload, "video"))
            throw new ArgumentException($"Bria {model} requires a video input.", nameof(request));

        switch (model)
        {
            case "video/edit/erase":
            {
                var mask = references.FirstOrDefault(static file => IsMediaType(file, "video/") || IsMediaType(file, "image/"));
                if (mask is not null)
                {
                    payload["mask"] = new Dictionary<string, object?> { ["mask_url"] = ToBriaMediaValue(mask) };
                    references.Remove(mask);
                }
                else if (!HasMask(payload))
                {
                    throw new ArgumentException("Bria video eraser requires a mask input or providerOptions.bria.mask.", nameof(request));
                }

                MapGenerateAudioToPreserveAudio(request, payload);
                break;
            }
            case "video/edit/replace_background":
            {
                var background = references.FirstOrDefault(static file =>
                    IsMediaType(file, "image/") || IsMediaType(file, "video/"));
                if (background is not null)
                {
                    payload["background_url"] = ToBriaMediaValue(background);
                    references.Remove(background);
                }
                else if (!HasUsableString(payload, "background_url"))
                {
                    throw new ArgumentException(
                        "Bria video background replacement requires an image/video inputReference or providerOptions.bria.background_url.",
                        nameof(request));
                }

                MapGenerateAudioToPreserveAudio(request, payload);
                break;
            }
            case "video/segment/mask_by_prompt":
                if (!string.IsNullOrWhiteSpace(request.Prompt))
                    payload["prompt"] = request.Prompt;
                else if (!HasUsableString(payload, "prompt"))
                    throw new ArgumentException("Bria mask-by-prompt requires a prompt.", nameof(request));
                break;
            case "video/segment/mask_by_key_points":
                if (!payload.ContainsKey("frame_index") || !payload.ContainsKey("key_points"))
                    throw new ArgumentException(
                        "Bria mask-by-key-points requires providerOptions.bria.frame_index and providerOptions.bria.key_points.",
                        nameof(request));
                break;
            case "video/edit/increase_resolution":
            case "video/edit/green_screen":
                MapGenerateAudioToPreserveAudio(request, payload);
                break;
        }

        AddBriaUnsupportedWarnings(model, request, references, warnings);
        return payload;
    }

    private static Dictionary<string, object?> GetBriaVideoPassthroughOptions(VideoRequest request)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var options = request.GetProviderMetadata<JsonElement>("bria");
        if (options.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in options.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private static void AddBriaUnsupportedWarnings(
        string model,
        VideoRequest request,
        IReadOnlyCollection<VideoFile> unusedReferences,
        List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt) && model != "video/segment/mask_by_prompt")
            warnings.Add(new { type = "unsupported", feature = "prompt" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (request.Image is not null && !IsMediaType(request.Image, "video/"))
            warnings.Add(new { type = "unsupported", feature = "image.nonVideo" });
        if (unusedReferences.Count > 0)
            warnings.Add(new { type = "unsupported", feature = "inputReferences.unused" });
        if (request.GenerateAudio is not null
            && model is "video/edit/remove_background"
                or "video/segment/mask_by_prompt"
                or "video/segment/mask_by_key_points"
                or "video/segment/foreground_mask")
            warnings.Add(new { type = "unsupported", feature = "generateAudio" });
    }

    private static void MapGenerateAudioToPreserveAudio(VideoRequest request, Dictionary<string, object?> payload)
    {
        if (request.GenerateAudio is not null)
            payload["preserve_audio"] = request.GenerateAudio.Value;
    }

    private static string NormalizeBriaVideoModel(string model)
    {
        var normalized = model.Trim().Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("bria/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["bria/".Length..];
        return normalized;
    }

    private static string ToBriaMediaValue(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Bria video inputs cannot contain empty data.", nameof(file));
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)))
            return file.Data;
        return $"data:{file.MediaType};base64,{file.Data}";
    }

    private static bool IsMediaType(VideoFile? file, string prefix)
        => file is not null
           && !string.IsNullOrWhiteSpace(file.MediaType)
           && file.MediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool HasUsableString(IReadOnlyDictionary<string, object?> payload, string property)
    {
        if (!payload.TryGetValue(property, out var value) || value is null)
            return false;
        return value switch
        {
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement { ValueKind: JsonValueKind.String } element => !string.IsNullOrWhiteSpace(element.GetString()),
            _ => true
        };
    }

    private static bool HasMask(IReadOnlyDictionary<string, object?> payload)
        => payload.TryGetValue("mask", out var mask)
           && mask is not null
           && (mask is not JsonElement element || element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);

    private string ResolveBriaPollUrl(BriaVideoOperationData operation)
    {
        if (!string.IsNullOrWhiteSpace(operation.StatusUrl)
            && Uri.TryCreate(operation.StatusUrl, UriKind.Absolute, out var statusUri)
            && string.Equals(statusUri.Host, _client.BaseAddress?.Host, StringComparison.OrdinalIgnoreCase))
            return statusUri.AbsoluteUri;
        return $"status/{Uri.EscapeDataString(operation.RequestId)}";
    }

    private static string EncodeBriaVideoOperation(BriaVideoOperationData operation)
    {
        var json = JsonSerializer.Serialize(operation, BriaJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return BriaVideoOperationTokenPrefix + base64Url;
    }

    private static BriaVideoOperationData DecodeBriaVideoOperation(string operation)
    {
        if (!operation.StartsWith(BriaVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new(Uri.UnescapeDataString(operation), null, null);

        var encoded = operation[BriaVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        var padding = encoded.Length % 4;
        if (padding != 0)
            encoded = encoded.PadRight(encoded.Length + (4 - padding), '=');
        try
        {
            var data = JsonSerializer.Deserialize<BriaVideoOperationData>(
                Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), BriaJson);
            if (data is null || string.IsNullOrWhiteSpace(data.RequestId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Bria video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The Bria video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string? FindBriaOutputUrl(JsonElement root)
    {
        foreach (var propertyName in new[] { "video_url", "mask_url", "output_url", "url" })
        {
            var value = FindStringProperty(root, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    private static string? FindStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindStringProperty(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringProperty(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string BuildBriaVideoError(JsonElement root, string status, string requestId)
    {
        var message = FindStringProperty(root, "message")
            ?? FindStringProperty(root, "details")
            ?? "Unknown Bria error.";
        var code = FindStringProperty(root, "code");
        return string.IsNullOrWhiteSpace(code)
            ? $"Bria video request '{requestId}' ended with status '{status}': {message}"
            : $"Bria video request '{requestId}' ended with status '{status}': {message} ({code})";
    }

    private static string GuessBriaVideoMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".gif" => "image/gif",
            _ => "video/mp4"
        };
    }

    private sealed record BriaVideoOperationData(string RequestId, string? StatusUrl, string? Model);
}
