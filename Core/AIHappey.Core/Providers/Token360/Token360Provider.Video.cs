using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Token360;

public partial class Token360Provider
{
    private const string Token360VideoOperationPrefix = "t360v1_";
    private static readonly JsonSerializerOptions Token360VideoJson = new(JsonSerializerDefaults.Web)
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

        List<object> warnings = [];
        var payload = BuildToken360VideoPayload(request, warnings);
        var json = JsonSerializer.Serialize(payload, Token360VideoJson);
        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token360 video submission failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var videoId = TryGetToken360String(root, "id")
            ?? throw new InvalidOperationException("Token360 video submission returned no id.");
        var model = NormalizeToken360Model(request.Model);

        return new VideoOperationStartResult
        {
            Operation = EncodeToken360VideoOperation(videoId, model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeToken360VideoOperation(operation);
        ApplyAuthHeader();
        using var pollResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.VideoId)}",
            cancellationToken);
        var raw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token360 video polling failed ({(int)pollResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = TryGetToken360String(root, "status") ?? "unknown";
        var model = TryGetToken360String(root, "model") ?? operationData.Model;
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = pollResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = GetToken360VideoError(root, operationData.VideoId),
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        using var downloadResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.VideoId)}/content?format=binary",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var video = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token360 video download failed ({(int)downloadResponse.StatusCode}): {Encoding.UTF8.GetString(video)}");

        // Token360 content is removed only after the complete video has been read successfully.
        using var deleteResponse = await _client.DeleteAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.VideoId)}",
            cancellationToken);
        var deleteRaw = await deleteResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!deleteResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token360 video deletion failed ({(int)deleteResponse.StatusCode}): {deleteRaw}");

        responseData.Headers = downloadResponse.GetHeaders();
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(video),
                    MediaType = downloadResponse.Content.Headers.ContentType?.MediaType
                        ?? GuessToken360VideoMediaType(TryGetToken360VideoUrl(root))
                        ?? "video/mp4"
                }
            ],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                poll = root,
                deleted = true
            }),
            Response = responseData
        };
    }

    private Dictionary<string, object?> BuildToken360VideoPayload(VideoRequest request, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        var options = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var rawOptions) == true
            ? rawOptions
            : default(JsonElement?);
        MergeToken360JsonOptions(payload, options);

        payload["model"] = NormalizeToken360Model(request.Model);
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null) payload["duration"] = request.Duration.Value;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.GenerateAudio is not null) payload["generate_audio"] = request.GenerateAudio.Value;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        if (request.Fps is not null) payload["fps"] = request.Fps.Value;
        if (request.N is not null) payload["sample_count"] = request.N.Value;

        var frames = request.FrameImages?.Select(ToToken360FrameImage).Cast<object>().ToList() ?? [];
        if (request.Image is not null && frames.Count == 0)
            frames.Add(ToToken360FrameImage(new VideoFrameImage { FrameType = "first_frame", Image = request.Image }));
        if (frames.Count > 0)
            payload["frame_images"] = frames;

        if (request.InputReferences?.Any() == true)
            payload["input_references"] = request.InputReferences.Select(ToToken360InputReference).ToArray();

        return payload;
    }

    private static Dictionary<string, object?> ToToken360FrameImage(VideoFrameImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Image);
        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["frame_type"] = frame.FrameType,
            ["image_url"] = new Dictionary<string, string> { ["url"] = NormalizeToken360MediaData(frame.Image) }
        };
    }

    private static Dictionary<string, object?> ToToken360InputReference(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var media = file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true
            ? "video"
            : file.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                ? "audio"
                : "image";
        return new Dictionary<string, object?>
        {
            ["type"] = media + "_url",
            [media + "_url"] = new Dictionary<string, string> { ["url"] = NormalizeToken360MediaData(file) }
        };
    }

    private static string NormalizeToken360MediaData(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Video reference data is required.", nameof(file));
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType;
        return $"data:{mediaType};base64,{file.Data}";
    }

    private static string EncodeToken360VideoOperation(string videoId, string model)
    {
        var envelope = JsonSerializer.Serialize(new Token360VideoOperationData(videoId, model), Token360VideoJson);
        return Token360VideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static Token360VideoOperationData DecodeToken360VideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(Token360VideoOperationPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Token360 video operation token is invalid. Start a new operation to obtain an opaque model-aware token.",
                nameof(operation));
        }

        var encoded = operation[Token360VideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (encoded.Length % 4 is var remainder && remainder != 0)
            encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var data = JsonSerializer.Deserialize<Token360VideoOperationData>(json, Token360VideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.VideoId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Token360 video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Token360 video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The Token360 video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string GetToken360VideoError(JsonElement root, string videoId)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
                return error.GetString()!;
            if (error.ValueKind == JsonValueKind.Object)
                return TryGetToken360String(error, "message") ?? error.GetRawText();
        }
        return $"Token360 video generation '{videoId}' failed.";
    }

    private static string? TryGetToken360VideoUrl(JsonElement root)
        => TryGetToken360String(root, "video_url") ?? TryGetToken360String(root, "url");

    private static string? GuessToken360VideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
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

    private sealed record Token360VideoOperationData(string VideoId, string Model);
}
