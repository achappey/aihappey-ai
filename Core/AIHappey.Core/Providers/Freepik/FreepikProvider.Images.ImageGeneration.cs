using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Freepik;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private const string ClassicFastPath = "/v1/ai/text-to-image";
    private const string FluxDevPath = "/v1/ai/text-to-image/flux-dev";
    private const string FluxProV11Path = "/v1/ai/text-to-image/flux-pro-v1-1";
    private const string HyperfluxPath = "/v1/ai/text-to-image/hyperflux";
    private const string SeedreamPath = "/v1/ai/text-to-image/seedream";
    private const string SeedreamV4Path = "/v1/ai/text-to-image/seedream-v4";
    private const string SeedreamV4EditPath = "/v1/ai/text-to-image/seedream-v4-edit";
    private const string SeedreamV45Path = "/v1/ai/text-to-image/seedream-v4-5";
    private const string Flux2ProPath = "/v1/ai/text-to-image/flux-2-pro";
    private const string Flux2TurboPath = "/v1/ai/text-to-image/flux-2-turbo";
    private const string ZImagePath = "/v1/ai/text-to-image/z-image";
    private const string Seedream45EditPath = "/v1/ai/text-to-image/seedream-v4-5-edit";
    private const string MysticPath = "/v1/ai/mystic";

    private sealed record FreepikTextToImageTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    /// <summary>
    /// Freepik Classic Fast (sync) text-to-image handler.
    /// </summary>
    /// <remarks>
    /// Endpoint: <c>POST /v1/ai/text-to-image</c>.
    /// Response returns base64 images directly (no async task polling).
    /// </remarks>
    public async Task<ImageResponse> ClassicFastImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (!imageRequest.Model.Equals("classic-fast", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Classic Fast handler called for unsupported model '{imageRequest.Model}'.");
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Classic Fast is text-to-image; input images were ignored." });

        var metadata = imageRequest.GetImageProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var cfg = metadata?.ImageGeneration?.ClassicFast;

        // Request body (maps 1:1 to Freepik Classic Fast)
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = imageRequest.Prompt
        };

        if (!string.IsNullOrWhiteSpace(cfg?.NegativePrompt))
            payload["negative_prompt"] = cfg!.NegativePrompt;

        if (cfg?.GuidanceScale is { } gs)
            payload["guidance_scale"] = gs;

        // Seed: ImageRequest.Seed (public contract) overrides providerOptions seed if both are set.
        if (imageRequest.Seed is not null)
            payload["seed"] = imageRequest.Seed;

        if (imageRequest.N is { } n && n != 1)
        {
            warnings.Add(new { type = "compatibility", feature = "n", details = "Classic Fast supports num_images via providerOptions.freepik.image_generation.classicfast.num_images; request.n was ignored." });
        }

        if (!string.IsNullOrWhiteSpace(cfg?.Image?.Size))
            payload["image"] = new Dictionary<string, object?> { ["size"] = cfg!.Image!.Size };

        if (cfg?.Styling is not null)
        {
            var styling = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(cfg.Styling.Style))
                styling["style"] = cfg.Styling.Style;

            if (cfg.Styling.Effects is not null)
            {
                var effects = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(cfg.Styling.Effects.Color))
                    effects["color"] = cfg.Styling.Effects.Color;
                if (!string.IsNullOrWhiteSpace(cfg.Styling.Effects.Lightning))
                    effects["lightning"] = cfg.Styling.Effects.Lightning;
                if (!string.IsNullOrWhiteSpace(cfg.Styling.Effects.Framing))
                    effects["framing"] = cfg.Styling.Effects.Framing;
                if (effects.Count > 0)
                    styling["effects"] = effects;
            }

            if (cfg.Styling.Colors is { Count: > 0 })
            {
                styling["colors"] = cfg.Styling.Colors
                    .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Color))
                    .Select(c => new Dictionary<string, object?>
                    {
                        ["color"] = c.Color,
                        ["weight"] = c.Weight
                    })
                    .ToList();
            }

            if (styling.Count > 0)
                payload["styling"] = styling;
        }

        // filter_nsfw defaults to true upstream; only send when explicitly specified.
        if (cfg?.FilterNsfw is { } filterNsfw)
            payload["filter_nsfw"] = filterNsfw;

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ClassicFastPath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik Classic Fast error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        var images = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var b64 = item.TryGetProperty("base64", out var b64El) ? b64El.GetString() : null;
                if (string.IsNullOrWhiteSpace(b64))
                    continue;

                images.Add($"data:image/png;base64,{b64}");

                // Keep it simple for now: one output image.
                break;
            }
        }

        if (images.Count == 0)
            throw new Exception("Freepik Classic Fast response missing data[].base64");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    /// <summary>
    /// Freepik text-to-image / image-to-image (async task) handler.
    /// Supported models: flux-dev, flux-pro-v1-1, hyperflux, seedream, seedream-v4, seedream-v4-edit,
    /// seedream-v4-5, seedream-v4-5-edit, flux-2-pro, flux-2-turbo, z-image, z-image-turbo, mystic/*.
    /// </summary>
    /// <remarks>
    /// Input images must be provided as raw base64 (no URLs, no downloading).
    /// Output images are downloaded from Freepik's generated URLs and returned as data URLs.
    /// </remarks>
    public async Task<ImageResponse> ImageGenerationImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.N is not null && imageRequest.N.Value != 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik text-to-image returns one image per request; generated a single image." });

        var metadata = imageRequest.GetImageProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var ig = metadata?.ImageGeneration;

        var (endpointPath, payload) = BuildTextToImagePayload(imageRequest, ig, warnings);
        var startJson = JsonSerializer.Serialize(payload, JsonOpts);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpointPath)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik text-to-image start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => TextToImagePollAsync(endpointPath, taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromMilliseconds(800),
            timeout: TimeSpan.FromSeconds(90),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik text-to-image task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik text-to-image task completed but returned no generated URLs (task_id={taskId}).");

        // Download final asset(s) and return as data URLs (contract).
        var images = new List<string>();
        foreach (var url in final.Generated ?? [])
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var fileResp = await _client.GetAsync(url, cancellationToken);
            var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!fileResp.IsSuccessStatusCode)
            {
                var err = Encoding.UTF8.GetString(fileBytes);
                throw new Exception($"Freepik download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
            }

            var mime = fileResp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mime))
                mime = "image/png";

            images.Add($"data:{mime};base64,{Convert.ToBase64String(fileBytes)}");

            // Keep it simple for now: one output image.
            break;
        }

        using var finalDoc = JsonDocument.Parse(final.Raw);

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = finalDoc.RootElement.Clone()
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["freepik"] = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    status = final.Status,
                    generated = final.Generated
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private static (string endpointPath, Dictionary<string, object?> payload) BuildTextToImagePayload(
        ImageRequest imageRequest,
        AIHappey.Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        List<object> warnings)
    {
        var model = imageRequest.Model.Trim();
        var isMystic = model.StartsWith("mystic/", StringComparison.OrdinalIgnoreCase);

        var endpoint = ResolveTextToImageEndpoint(model, isMystic);

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = imageRequest.Prompt
            // webhook_url intentionally omitted (per user instruction: polling)
        };

        // Common contract fields
        // aspect_ratio: map from public contract when the model supports it.
        if (!isMystic && ModelSupportsAspectRatio(model) && !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            payload["aspect_ratio"] = imageRequest.AspectRatio;

        // Common contract fields
        if (imageRequest.Seed is { } seed)
        {
            if (isMystic)
                warnings.Add(new { type = "unsupported", feature = "seed", details = "Mystic does not support a seed parameter; seed was ignored." });
            else
                payload["seed"] = seed;
        }

        if (isMystic)
            return BuildMysticPayload(endpoint, model, imageRequest, imageGeneration, payload, warnings);

        // Model-specific mapping (split across partials)
        if (model.Equals("flux-dev", StringComparison.OrdinalIgnoreCase))
            ApplyFluxDevPayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("flux-pro-v1-1", StringComparison.OrdinalIgnoreCase))
            ApplyFluxProV11Payload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("hyperflux", StringComparison.OrdinalIgnoreCase))
            ApplyHyperfluxPayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("seedream", StringComparison.OrdinalIgnoreCase))
            ApplySeedreamPayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("seedream-v4", StringComparison.OrdinalIgnoreCase))
            ApplySeedreamV4Payload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("seedream-v4-edit", StringComparison.OrdinalIgnoreCase))
            ApplySeedreamV4EditPayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("seedream-v4-5", StringComparison.OrdinalIgnoreCase))
            ApplySeedreamV45Payload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("flux-2-pro", StringComparison.OrdinalIgnoreCase))
            ApplyFlux2ProPayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("flux-2-turbo", StringComparison.OrdinalIgnoreCase))
            ApplyFlux2TurboPayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("z-image", StringComparison.OrdinalIgnoreCase) || model.Equals("z-image-turbo", StringComparison.OrdinalIgnoreCase))
            ApplyZImagePayload(imageRequest, imageGeneration, payload, warnings);
        else if (model.Equals("seedream-v4-5-edit", StringComparison.OrdinalIgnoreCase))
            ApplySeedream45EditPayload(imageRequest, imageGeneration, payload, warnings);

        return (endpoint, payload);
    }

    private static string ResolveTextToImageEndpoint(string model, bool isMystic)
    {
        if (isMystic)
            return MysticPath;

        return model.ToLowerInvariant() switch
        {
            "flux-dev" => FluxDevPath,
            "flux-pro-v1-1" => FluxProV11Path,
            "hyperflux" => HyperfluxPath,
            "seedream" => SeedreamPath,
            "seedream-v4" => SeedreamV4Path,
            "seedream-v4-edit" => SeedreamV4EditPath,
            "seedream-v4-5" => SeedreamV45Path,
            "flux-2-pro" => Flux2ProPath,
            "flux-2-turbo" => Flux2TurboPath,
            "z-image" => ZImagePath,
            "z-image-turbo" => ZImagePath,
            "seedream-v4-5-edit" => Seedream45EditPath,
            _ => throw new NotSupportedException($"Freepik image model '{model}' is not supported.")
        };
    }

    private static bool ModelSupportsAspectRatio(string model)
    {
        // Based on Freepik docs (llms.txt).
        // Note: z-image uses image_size instead, flux-2* use width/height.
        return model.Equals("flux-dev", StringComparison.OrdinalIgnoreCase)
            || model.Equals("flux-pro-v1-1", StringComparison.OrdinalIgnoreCase)
            || model.Equals("hyperflux", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedream", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedream-v4", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedream-v4-edit", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedream-v4-5", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedream-v4-5-edit", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<FreepikTextToImageTaskResult> TextToImagePollAsync(string endpointPath, string taskId, CancellationToken cancellationToken)
    {
        // Freepik task endpoints use the same path with /{task-id}
        var url = $"{BaseUrl}{endpointPath}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik text-to-image poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        var status = data.GetProperty("status").GetString() ?? "UNKNOWN";
        var returnedTaskId = data.TryGetProperty("task_id", out var idEl) ? (idEl.GetString() ?? taskId) : taskId;

        List<string>? generated = null;
        if (data.TryGetProperty("generated", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            generated = [.. genEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))];
        }

        return new FreepikTextToImageTaskResult(status, generated, raw, returnedTaskId);
    }
}

