using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.ImageRouter;

public partial class ImageRouterProvider
{
    private async Task<VideoResponse> VideoRequestImageRouter(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var warnings = BuildVideoWarnings(request);
        var startedAt = DateTime.UtcNow;

        using var httpRequest = CreateVideoRequestMessage(request, warnings);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"ImageRouter API error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var document = JsonDocument.Parse(raw);
        var completed = await EnsureTerminalImageRouterResponseAsync(document.RootElement.Clone(), cancellationToken);

        ThrowIfImageRouterError(completed, "video generation");

        var videos = await ExtractVideoOutputsAsync(completed, cancellationToken);
        if (videos.Count == 0)
            throw new InvalidOperationException("ImageRouter video generation returned no output.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = BuildProviderMetadata(completed),
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model,
                Body = completed
            }
        };
    }

    private HttpRequestMessage CreateVideoRequestMessage(VideoRequest request, List<object> warnings)
    {
        var payload = BuildVideoPayload(request, warnings);

        if (request.Image is null)
        {
            var body = JsonSerializer.Serialize(payload, ImageRouterJsonOptions);
            return new HttpRequestMessage(HttpMethod.Post, "v1/openai/videos/generations")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        var multipart = new MultipartFormDataContent();

        foreach (var entry in payload)
            AddMultipartValue(multipart, entry.Key, entry.Value);

        multipart.Add(CreateFileContent(request.Image), "image[]", GetFileName(request.Image.MediaType, "image"));

        return new HttpRequestMessage(HttpMethod.Post, "v1/openai/videos/generations")
        {
            Content = multipart
        };
    }

    private Dictionary<string, object?> BuildVideoPayload(VideoRequest request, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["response_format"] = ResolveBase64ResponseFormat(request.ProviderOptions)
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        var size = ResolveVideoSize(request, warnings);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (request.Duration.HasValue)
            payload["seconds"] = request.Duration.Value;

        MergeRawProviderOptions(payload, request.ProviderOptions);

        payload["model"] = request.Model;
        payload["response_format"] = ResolveBase64ResponseFormat(request.ProviderOptions);

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (request.Duration.HasValue)
            payload["seconds"] = request.Duration.Value;

        return payload;
    }

    private List<object> BuildVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", property = "seed" });

        if (request.N.HasValue)
            warnings.Add(new { type = "unsupported", property = "n" });

        if (request.Fps.HasValue)
            warnings.Add(new { type = "unsupported", property = "fps" });

        return warnings;
    }

    private static string? ResolveVideoSize(VideoRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            return request.Resolution;

        if (TryResolveAspectRatioSize(request.AspectRatio, 1280, 1280, out var inferred))
        {
            warnings.Add(new
            {
                type = "mapped_property",
                property = "aspectRatio",
                mappedTo = "size",
                value = inferred
            });

            return inferred;
        }

        return null;
    }
}
