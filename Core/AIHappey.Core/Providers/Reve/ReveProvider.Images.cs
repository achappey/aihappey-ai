using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Reve;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Reve;

public partial class ReveProvider
{
    private static readonly JsonSerializerOptions ReveJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<ReveImageProviderMetadata>(GetIdentifier());

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Reve returns exactly 1 image." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var files = request.Files?.ToList() ?? [];
        var hasFiles = files.Count > 0;
        var isMultiFile = files.Count > 1;

        var (endpoint, payload) = BuildRevePayload(request, metadata, warnings, hasFiles, isMultiFile);

        var json = JsonSerializer.Serialize(payload, ReveJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        req.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("image", out var imageEl))
            throw new Exception("Reve response did not include an image.");

        var image = imageEl.GetString();
        if (string.IsNullOrWhiteSpace(image))
            throw new Exception("Reve response contained empty image data.");

        return new ImageResponse
        {
            Images = [image.ToDataUrl(MediaTypeNames.Image.Png)],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static (string Endpoint, object Payload) BuildRevePayload(
        ImageRequest request,
        ReveImageProviderMetadata? metadata,
        List<object> warnings,
        bool hasFiles,
        bool isMultiFile)
    {
        var model = request.Model.Trim();
        var isLatest = model.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || model.Equals("latest-fast", StringComparison.OrdinalIgnoreCase);

        if (isLatest)
        {
            if (isMultiFile)
                return ("v1/image/remix", BuildRemixPayload(request, metadata, versionOverride: model));

            if (hasFiles)
                return ("v1/image/edit", BuildEditPayload(request, metadata, warnings, versionOverride: model));

            return ("v1/image/create", BuildCreatePayload(request, metadata, versionOverride: model));
        }

        if (model.StartsWith("reve-create@", StringComparison.OrdinalIgnoreCase))
            return ("v1/image/create", BuildCreatePayload(request, metadata, versionOverride: model));

        if (model.StartsWith("reve-edit@", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("reve-edit-fast@", StringComparison.OrdinalIgnoreCase))
            return ("v1/image/edit", BuildEditPayload(request, metadata, warnings, versionOverride: model));

        if (model.StartsWith("reve-remix@", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("reve-remix-fast@", StringComparison.OrdinalIgnoreCase))
            return ("v1/image/remix", BuildRemixPayload(request, metadata, versionOverride: model));

        return ("v1/image/create", BuildCreatePayload(request, metadata, versionOverride: model));
    }

    private static object BuildCreatePayload(
        ImageRequest request,
        ReveImageProviderMetadata? metadata,
        string? versionOverride)
        => new
        {
            prompt = request.Prompt,
            aspect_ratio = request.AspectRatio,
            version = versionOverride,
            test_time_scaling = metadata?.TestTimeScaling,
            postprocessing = metadata?.Postprocessing
        };

    private static object BuildEditPayload(
        ImageRequest request,
        ReveImageProviderMetadata? metadata,
        List<object> warnings,
        string? versionOverride)
    {
        var file = request.Files?.FirstOrDefault();
        if (file is null)
            throw new ArgumentException("reference image is required for Reve edit.", nameof(request));

        if (request.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images not supported for edit; used files[0]."
            });
        }

        var imageData = file.Data;
        if (imageData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            imageData = imageData.RemoveDataUrlPrefix();

        return new
        {
            edit_instruction = request.Prompt,
            reference_image = imageData,
            aspect_ratio = request.AspectRatio,
            version = versionOverride,
            test_time_scaling = metadata?.TestTimeScaling,
            postprocessing = metadata?.Postprocessing
        };
    }

    private static object BuildRemixPayload(
        ImageRequest request,
        ReveImageProviderMetadata? metadata,
        string? versionOverride)
    {
        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("reference_images are required for Reve remix.", nameof(request));

        var images = files
            .Select(file =>
            {
                var data = file.Data;
                return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? data.RemoveDataUrlPrefix()
                    : data;
            })
            .ToArray();

        return new
        {
            prompt = request.Prompt,
            reference_images = images,
            aspect_ratio = request.AspectRatio,
            version = versionOverride,
            test_time_scaling = metadata?.TestTimeScaling,
            postprocessing = metadata?.Postprocessing
        };
    }
}

