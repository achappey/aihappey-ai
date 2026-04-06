using System.Net.Mime;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    private async Task<VideoResponse> VideoRequestApiAirforce(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var model = NormalizeModelId(request.Model);
        var now = DateTime.UtcNow;
        var warnings = BuildVideoWarnings(request, model);
        var providerOptions = TryGetProviderOptions(request.ProviderOptions, GetIdentifier());
        var responseFormat = ResolveResponseFormat(providerOptions, "url")!;

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "model", "prompt", "response_format", "resolution", "aspectRatio", "duration", "image_urls", "wan_image_url"
        };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["response_format"] = responseFormat
        };

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspectRatio"] = request.AspectRatio;

        if (request.Duration is > 0 && model.StartsWith("wan-", StringComparison.OrdinalIgnoreCase))
            payload["duration"] = request.Duration.Value;

        if (request.Image is not null)
        {
            if (model.StartsWith("wan-", StringComparison.OrdinalIgnoreCase))
                payload["wan_image_url"] = ToDataUrl(request.Image);
            else
                payload["image_urls"] = new[] { ToDataUrl(request.Image) };
        }

        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        payload["model"] = model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = responseFormat;

        var root = await SendMediaGenerationAsync(payload, cancellationToken);
        var videos = await ExtractVideosAsync(root, cancellationToken);

        if (videos.Count == 0)
            throw new InvalidOperationException("ApiAirforce video generation returned no video outputs.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static List<object> BuildVideoWarnings(VideoRequest request, string model)
    {
        var warnings = new List<object>();

        if (request.Seed is not null)
            AddUnsupportedWarning(warnings, "seed", "ApiAirforce docs do not publish a generic seed parameter for video generation.");

        if (request.Fps is not null)
            AddUnsupportedWarning(warnings, "fps");

        if (request.N is not null)
            AddUnsupportedWarning(warnings, "n", "ApiAirforce docs do not publish multi-video generation for these models.");

        if (request.Duration is not null && !model.StartsWith("wan-", StringComparison.OrdinalIgnoreCase))
            AddUnsupportedWarning(warnings, "duration", "Only Wan models document duration.");

        return warnings;
    }

    private async Task<List<VideoResponseFile>> ExtractVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var videos = new List<VideoResponseFile>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var item in dataEl.EnumerateArray())
        {
            var base64 = TryGetString(item, "b64_json")
                ?? TryGetString(item, "base64")
                ?? TryGetString(item, "data");

            if (!string.IsNullOrWhiteSpace(base64))
            {
                videos.Add(new VideoResponseFile
                {
                    Type = "base64",
                    Data = base64,
                    MediaType = "video/mp4"
                });
                continue;
            }

            var url = TryGetString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var downloaded = await TryFetchAsBase64Async(url, cancellationToken);
            if (downloaded is not null)
            {
                videos.Add(new VideoResponseFile
                {
                    Type = "base64",
                    Data = downloaded.Value.Base64,
                    MediaType = downloaded.Value.MediaType
                });
                continue;
            }

            videos.Add(new VideoResponseFile
            {
                Type = "base64",
                Data = url,
                MediaType = GuessMediaTypeFromUrl(url, "video/mp4")
            });
        }

        return videos;
    }
}
