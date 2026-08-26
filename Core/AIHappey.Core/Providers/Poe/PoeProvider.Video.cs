using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Poe;

public partial class PoeProvider
{
    private const string PoeVideoOperationTokenPrefix = "poev1_";

    private static readonly JsonSerializerOptions PoeVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var model = NormalizePoeVideoModel(request.Model);
        var warnings = GetPoeVideoWarnings(request);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["seconds"] = request.Duration,
            ["size"] = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution,
            ["input_image"] = request.Image is null ? null : NormalizePoeInputImage(request.Image)
        };

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, PoeVideoJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var createRoot = await ReadPoeVideoJsonAsync(createResponse, "creation", cancellationToken);
        var videoId = GetPoeVideoString(createRoot, "id");
        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("Poe video creation did not return an id.");

        return new VideoOperationStartResult
        {
            Operation = EncodePoeVideoOperation(videoId, model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(createRoot),
            Response = new HeaderResponseData
            {
                Timestamp = ReadPoeVideoTimestamp(createRoot, "created_at") ?? DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodePoeVideoOperation(operation);
        ApplyAuthHeader();

        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/videos/{Uri.EscapeDataString(operationData.VideoId)}");
        using var statusResponse = await _client.SendAsync(
            statusRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var statusRoot = await ReadPoeVideoJsonAsync(statusResponse, "status", cancellationToken);
        var status = GetPoeVideoString(statusRoot, "status");
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusRoot);
        var response = new HeaderResponseData
        {
            Timestamp = ReadPoeVideoTimestamp(statusRoot, "completed_at")
                ?? ReadPoeVideoTimestamp(statusRoot, "created_at")
                ?? DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = ReadPoeVideoError(statusRoot)
                    ?? $"Poe video '{operationData.VideoId}' failed.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };
        }

        using var contentRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/videos/{Uri.EscapeDataString(operationData.VideoId)}/content");
        using var contentResponse = await _client.SendAsync(
            contentRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var bytes = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Poe video content download failed ({(int)contentResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        }

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(bytes),
                    MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4"
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string NormalizePoeVideoModel(string model)
    {
        var trimmed = model.Trim();
        var separator = trimmed.IndexOf('/');
        return separator >= 0
            && trimmed[..separator].Equals("poe", StringComparison.OrdinalIgnoreCase)
                ? trimmed[(separator + 1)..]
                : trimmed;
    }

    private static string NormalizePoeInputImage(VideoFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Poe input image data is required.", nameof(image));

        var data = image.Data.Trim();
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Poe input image data URLs must contain base64 data.", nameof(image));
            data = data[(comma + 1)..];
        }

        try
        {
            _ = Convert.FromBase64String(data);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Poe input image data must be valid base64.", nameof(image), exception);
        }

        return data;
    }

    private static List<object> GetPoeVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "input_references" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frame_images" });
        if (request.GenerateAudio is not null)
            warnings.Add(new { type = "unsupported", feature = "generate_audio" });
        return warnings;
    }

    private static string EncodePoeVideoOperation(string videoId, string model)
    {
        var json = JsonSerializer.Serialize(new PoeVideoOperationData(videoId, model), PoeVideoJson);
        return PoeVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static PoeVideoOperationData DecodePoeVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(PoeVideoOperationTokenPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A model-aware Poe video operation token is required.", nameof(operation));
        }

        try
        {
            var value = operation[PoeVideoOperationTokenPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var data = JsonSerializer.Deserialize<PoeVideoOperationData>(json, PoeVideoJson);
            if (data is null
                || string.IsNullOrWhiteSpace(data.VideoId)
                || string.IsNullOrWhiteSpace(data.Model))
            {
                throw new ArgumentException("The Poe video operation token is invalid.", nameof(operation));
            }

            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Poe video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static async Task<JsonElement> ReadPoeVideoJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Poe video {operation} failed ({(int)response.StatusCode})."
                : $"Poe video {operation} failed ({(int)response.StatusCode}): {raw}");
        }

        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"Poe video {operation} returned an empty response.");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string? GetPoeVideoString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ReadPoeVideoTimestamp(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;

    private static string? ReadPoeVideoError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "message", "detail", "code" })
            {
                var value = GetPoeVideoString(error, property);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        return error.GetRawText();
    }

    private sealed record PoeVideoOperationData(string VideoId, string Model);
}
