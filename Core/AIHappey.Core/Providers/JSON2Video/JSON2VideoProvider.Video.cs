using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.JSON2Video;

public partial class JSON2VideoProvider
{
    private const string JSON2VideoOperationTokenPrefix = "j2vv1_";

    private static readonly JsonSerializerOptions VideoJson = new(JsonSerializerDefaults.Web)
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

        var warnings = BuildJSON2VideoWarnings(request);
        var payload = BuildMoviePayload(request);

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v2/movies")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VideoJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"JSON2Video movie creation failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var root = createDocument.RootElement.Clone();
        var projectId = ReadJSON2VideoString(root, "project");
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("JSON2Video create response missing project id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeJSON2VideoOperation(projectId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadJSON2VideoTimestamp(root, "timestamp") ?? DateTime.UtcNow,
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

        var (projectId, model) = DecodeJSON2VideoOperation(operation);
        ApplyAuthHeader();

        var escapedProjectId = Uri.EscapeDataString(projectId);
        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v2/movies?project={escapedProjectId}&format=simple");
        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"JSON2Video movie status failed ({(int)statusResponse.StatusCode}): {statusRaw}");

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var statusRoot = statusDocument.RootElement.Clone();
        var movie = ReadJSON2VideoMovie(statusRoot);
        var status = ReadJSON2VideoString(movie, "status")?.Trim().ToLowerInvariant();
        var response = new HeaderResponseData
        {
            Timestamp = ReadJSON2VideoTimestamp(movie, "ended_at")
                ?? ReadJSON2VideoTimestamp(movie, "created_at")
                ?? DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };
        var statusMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusRoot);

        if (status is "error" or "timeout")
            return new VideoOperationErrorResult
            {
                Error = ReadJSON2VideoString(movie, "message")
                    ?? $"JSON2Video movie render failed (project={projectId}, status={status}).",
                ProviderMetadata = statusMetadata,
                Response = response
            };

        if (status != "done")
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = statusMetadata,
                Response = response
            };

        var videoUrl = ReadJSON2VideoString(movie, "url");
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult
            {
                Error = $"JSON2Video movie render finished but returned no URL (project={projectId}).",
                ProviderMetadata = statusMetadata,
                Response = response
            };

        using var videoResponse = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode || videoBytes.Length == 0)
            throw new InvalidOperationException(
                $"JSON2Video video download failed ({(int)videoResponse.StatusCode}): {Encoding.UTF8.GetString(videoBytes)}");

        var cleanup = await DeleteJSON2VideoMovieAsync(projectId, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new Dictionary<string, object?>
        {
            ["status"] = statusRoot,
            ["cleanup"] = cleanup
        });

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = videoResponse.Content.Headers.ContentType?.MediaType
                        ?? GuessJSON2VideoMediaType(videoUrl)
                        ?? "video/mp4",
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private async Task<object> DeleteJSON2VideoMovieAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            using var deleteRequest = new HttpRequestMessage(
                HttpMethod.Delete,
                $"v2/movies?project={Uri.EscapeDataString(projectId)}");
            using var deleteResponse = await _client.SendAsync(deleteRequest, cancellationToken);
            var raw = await deleteResponse.Content.ReadAsStringAsync(cancellationToken);
            JsonElement? body = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    body = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Preserve a non-JSON cleanup response below without failing
                    // an otherwise successful video operation.
                }
            }

            return new
            {
                success = deleteResponse.IsSuccessStatusCode,
                statusCode = (int)deleteResponse.StatusCode,
                body,
                raw = body is null ? raw : null
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new { success = false, error = "JSON2Video movie cleanup timed out." };
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return new { success = false, error = exception.Message };
        }
    }

    private static Dictionary<string, object?> BuildMoviePayload(VideoRequest request)
    {
        var payload = CopyJSON2VideoProviderMetadata(
            request.GetProviderMetadata<JsonElement>("json2video"));

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        var hasMovieContent = payload.ContainsKey("scenes")
            || payload.ContainsKey("elements")
            || payload.ContainsKey("template");
        if (!hasMovieContent)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException(
                    "Prompt is required when providerMetadata.json2video does not contain scenes, elements, or a template.",
                    nameof(request));

            payload["scenes"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["background-color"] = "#4392F1",
                    ["elements"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = request.Prompt,
                            ["duration"] = request.Duration is > 0 ? request.Duration.Value : 4
                        }
                    }
                }
            };
        }

        return payload;
    }

    private static Dictionary<string, object?> CopyJSON2VideoProviderMetadata(JsonElement source)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in source.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static List<object> BuildJSON2VideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Image is not null) warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.InputReferences?.Any() == true) warnings.Add(new { type = "unsupported", feature = "inputReferences" });
        if (request.FrameImages?.Any() == true) warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        return warnings;
    }

    private static string EncodeJSON2VideoOperation(string projectId, string model)
    {
        var json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["project"] = projectId, ["model"] = model },
            VideoJson);
        return JSON2VideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static (string ProjectId, string Model) DecodeJSON2VideoOperation(string operation)
    {
        if (!operation.StartsWith(JSON2VideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                "The JSON2Video video operation token is invalid. Start a new operation to obtain a model-aware token.",
                nameof(operation));

        try
        {
            var base64 = operation[JSON2VideoOperationTokenPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            if (base64.Length % 4 != 0)
                base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            var projectId = ReadJSON2VideoString(document.RootElement, "project");
            var model = ReadJSON2VideoString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The JSON2Video video operation token is invalid.", nameof(operation));
            return (projectId, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The JSON2Video video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static JsonElement ReadJSON2VideoMovie(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("movie", out var movie)
            && movie.ValueKind == JsonValueKind.Object)
            return movie;
        throw new InvalidOperationException("JSON2Video movie status response contained no movie object.");
    }

    private static string? ReadJSON2VideoString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static DateTime? ReadJSON2VideoTimestamp(JsonElement root, string propertyName)
        => DateTime.TryParse(ReadJSON2VideoString(root, propertyName), out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;

    private static string? GuessJSON2VideoMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        return null;
    }
}
