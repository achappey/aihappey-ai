using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Freepik;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private const string ReimagineFluxPath = "/v1/ai/beta/text-to-image/reimagine-flux";

    public async Task<ImageResponse> ReimagineFluxImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!imageRequest.Model.Equals("reimagine-flux", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Freepik image model '{imageRequest.Model}' is not supported.");

        // Compatibility warnings (match style of other Freepik handlers)
        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        // Freepik API expects aspect_ratio in the request body; we accept AspectRatio on the public contract
        // but do not map it (per user instruction: 1:1 mapping from backend via providerOptions).
        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            warnings.Add(new { type = "compatibility", feature = "aspect_ratio", details = "Use providerOptions.freepik.reimagine_flux.aspect_ratio for Reimagine Flux; request aspectRatio was ignored." });

        if (imageRequest.N is not null && imageRequest.N.Value != 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Freepik reimagine-flux returns one image per request; generated a single image." });

        // Input image is required.
        var files = imageRequest.Files?.ToList();
        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one input image is required in 'files'.", nameof(imageRequest));
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Freepik reimagine-flux supports a single input image; extra images were ignored." });

        var firstFile = files[0];
        if (string.IsNullOrWhiteSpace(firstFile?.Data))
            throw new ArgumentException("files[0] must include 'data' (base64).", nameof(imageRequest));

        // Requirement: raw base64 only (no data URLs).
        if (LooksLikeDataUrl(firstFile.Data))
            throw new ArgumentException("files[0].data must be raw base64 (data URLs are not supported for Freepik reimagine-flux).", nameof(imageRequest));

        // Prompt must come from the public contract surface.
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            warnings.Add(new { type = "compatibility", feature = "prompt", details = "Freepik reimagine-flux works best with a prompt; prompt was empty so only the image will be used." });

        var metadata = imageRequest.GetProviderMetadata<FreepikImageProviderMetadata>(GetIdentifier());
        var cfg = metadata?.ReimagineFlux;

        var payload = BuildReimagineFluxPayload(firstFile.Data, imageRequest.Prompt, cfg, warnings);
        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ReimagineFluxPath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik reimagine-flux error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");

        var taskId = data.GetProperty("task_id").GetString();
        var status = data.GetProperty("status").GetString() ?? "UNKNOWN";

        List<string>? generated = null;
        if (data.TryGetProperty("generated", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            generated = [.. genEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))];
        }

        var firstUrl = generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception("Freepik reimagine-flux response missing data.generated[0]");

        // Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik reimagine-flux download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
        }

        var mime = fileResp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mime))
            mime = "image/png";

        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(fileBytes)}";

        return new ImageResponse
        {
            Images = [dataUrl],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = doc.RootElement.Clone()
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["freepik"] = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    status,
                    generated
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private static Dictionary<string, object?> BuildReimagineFluxPayload(
        string image,
        string? prompt,
        ReimagineFlux? cfg,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["image"] = image
            // webhook_url intentionally omitted (per user instruction: ignore webhooks)
        };

        if (!string.IsNullOrWhiteSpace(prompt))
            payload["prompt"] = prompt;

        if (!string.IsNullOrWhiteSpace(cfg?.Imagination))
            payload["imagination"] = cfg.Imagination;

        if (!string.IsNullOrWhiteSpace(cfg?.AspectRatio))
            payload["aspect_ratio"] = cfg.AspectRatio;

        if (cfg is null)
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "reimagine_flux",
                details = "No reimagine_flux config provided; using API defaults."
            });
        }

        return payload;
    }
}

