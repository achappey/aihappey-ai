using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.JSON2Video;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.JSON2Video;

public partial class JSON2VideoProvider
{
    private static readonly JsonSerializerOptions VideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Json2VideoMovieStatus(string Status, JsonElement RawRoot);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        var metadata = request.GetProviderMetadata<JSON2VideoVideoProviderMetadata>(GetIdentifier());
        var movieJson = BuildMovieJson(request, metadata);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v2/movies")
        {
            Content = new StringContent(movieJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"JSON2Video movie creation failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var projectId = createRoot.TryGetProperty("project", out var projectEl) && projectEl.ValueKind == JsonValueKind.String
            ? projectEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("JSON2Video create response missing project id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollMovieAsync(projectId, ct),
            isTerminal: result => result.Status is "done" or "error",
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (completed.Status == "error")
        {
            var error = TryGetMovieMessage(completed.RawRoot) ?? "JSON2Video rendering failed.";
            throw new InvalidOperationException($"JSON2Video movie render failed (project={projectId}): {error}");
        }

        var videoUrl = TryGetMovieUrl(completed.RawRoot);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException($"JSON2Video movie render finished but returned no url (project={projectId}).");

        using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(videoBytes);
            throw new InvalidOperationException($"JSON2Video video download failed ({(int)videoResp.StatusCode}): {err}");
        }

        var mediaType = videoResp.Content.Headers.ContentType?.MediaType
            ?? GuessVideoMediaType(videoUrl)
            ?? "video/mp4";

        Dictionary<string, JsonElement>? providerMetadata = null;
        try
        {
            var meta = new Dictionary<string, JsonElement>
            {
                ["create"] = createRoot,
                ["status"] = completed.RawRoot.Clone()
            };

            providerMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(meta, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            // best-effort only
        }

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = createRoot
            }
        };
    }

    private static string BuildMovieJson(VideoRequest request, JSON2VideoVideoProviderMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.Movie))
            return metadata.Movie;

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required when providerOptions.json2video.movie is not supplied.", nameof(request));

        var duration = request.Duration is > 0 ? request.Duration.Value : 4;

        var payload = new Dictionary<string, object?>
        {
            ["resolution"] = request.Resolution ?? "full-hd",
            ["quality"] = "high",
            ["scenes"] = new object[]
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
                            ["duration"] = duration
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload, VideoJson);
    }

    private async Task<Json2VideoMovieStatus> PollMovieAsync(string projectId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v2/movies?project={Uri.EscapeDataString(projectId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"JSON2Video movie poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();

        var status = root.TryGetProperty("movie", out var movieEl)
            && movieEl.ValueKind == JsonValueKind.Object
            && movieEl.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? "unknown"
                : "unknown";

        return new Json2VideoMovieStatus(status, root);
    }

    private static string? TryGetMovieUrl(JsonElement root)
    {
        if (!root.TryGetProperty("movie", out var movieEl) || movieEl.ValueKind != JsonValueKind.Object)
            return null;

        if (!movieEl.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return null;

        return urlEl.GetString();
    }

    private static string? TryGetMovieMessage(JsonElement root)
    {
        if (!root.TryGetProperty("movie", out var movieEl) || movieEl.ValueKind != JsonValueKind.Object)
            return null;

        if (!movieEl.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != JsonValueKind.String)
            return null;

        return msgEl.GetString();
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }
}

