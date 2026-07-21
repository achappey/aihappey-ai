using AIHappey.Common.Model.Providers.Ideogram;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;

namespace AIHappey.Core.Providers.Ideogram;

public partial class IdeogramProvider
{
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();

        return model switch
        {
            "ideogram/ideogram-v3" => RouteV3Async(request, cancellationToken),
            "ideogram/ideogram-v4" => RouteV4Async(request, cancellationToken),
            "ideogram/ideogram-v3/generate-transparent" => GenerateTransparentV3Async(request, cancellationToken),
            "ideogram/ideogram-v3/reframe" => ReframeV3Async(request, cancellationToken),
            "ideogram/ideogram-v3/replace-background" => ReplaceBackgroundV3Async(request, cancellationToken),
            "ideogram/upscale" => UpscaleAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Ideogram image model '{model}' is not supported.")
        };
    }

    private Task<ImageResponse> RouteV3Async(ImageRequest request, CancellationToken ct)
    {
        var hasSourceImage = request.Files?.Any() == true;
        if (!hasSourceImage && request.Mask is not null)
            throw new ArgumentException("Ideogram image edit requires at least one source image.", nameof(request));

        return request.Mask is not null
            ? EditV3Async(request, ct)
            : hasSourceImage
                ? RemixV3Async(request, ct)
                : GenerateV3Async(request, ct);
    }

    private Task<ImageResponse> RouteV4Async(ImageRequest request, CancellationToken ct)
    {
        if (request.Mask is not null)
        {
            throw new NotSupportedException(
                "Ideogram 4.0 does not provide a masked edit endpoint. Use ideogram/ideogram-v3 for image edits.");
        }

        return request.Files?.Any() == true
            ? RemixV4Async(request, ct)
            : GenerateV4Async(request, ct);
    }

    private async Task<ImageResponse> GenerateV3Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildGenerateForm(request, metadata, warnings, transparent: false);

        return await SendV3FormAsync(
            request,
            warnings,
            endpoint: "v1/ideogram-v3/generate",
            form,
            ct);
    }

    private async Task<ImageResponse> GenerateTransparentV3Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildGenerateTransparentForm(request, metadata, warnings);

        return await SendV3FormAsync(
            request,
            warnings,
            endpoint: "v1/ideogram-v3/generate-transparent",
            form,
            ct);
    }

    private async Task<ImageResponse> EditV3Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildEditForm(request, metadata, warnings);

        return await SendV3FormAsync(
            request,
            warnings,
            endpoint: "v1/edit",
            form,
            ct);
    }

    private async Task<ImageResponse> RemixV3Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildRemixForm(request, metadata, warnings);

        return await SendV3FormAsync(
            request,
            warnings,
            endpoint: "v1/ideogram-v3/remix",
            form,
            ct);
    }

    private async Task<ImageResponse> GenerateV4Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildGenerateV4Form(request, metadata, warnings);

        return await SendV3FormAsync(request, warnings, "v1/ideogram-v4/generate", form, ct);
    }

    private async Task<ImageResponse> RemixV4Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildRemixV4Form(request, metadata, warnings);

        return await SendV3FormAsync(request, warnings, "v1/ideogram-v4/remix", form, ct);
    }

    private async Task<ImageResponse> ReframeV3Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildReframeForm(request, metadata, warnings);

        return await SendV3FormAsync(
            request,
            warnings,
            endpoint: "v1/ideogram-v3/reframe",
            form,
            ct);
    }

    private async Task<ImageResponse> ReplaceBackgroundV3Async(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildReplaceBackgroundForm(request, metadata, warnings);

        return await SendV3FormAsync(
            request,
            warnings,
            endpoint: "v1/ideogram-v3/replace-background",
            form,
            ct);
    }

    private async Task<ImageResponse> UpscaleAsync(ImageRequest request, CancellationToken ct)
    {
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<IdeogramImageProviderMetadata>(GetIdentifier());
        var form = BuildUpscaleForm(request, metadata, warnings);

        return await SendUpscaleFormAsync(request, warnings, form, ct);
    }

    private static MultipartFormDataContent BuildGenerateForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings,
        bool transparent)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var form = new MultipartFormDataContent
        {
            "prompt".NamedField(request.Prompt)
        };
        AddSeed(request, form);

        var resolution = ResolveResolution(request, metadata, warnings, allowResolution: !transparent);
        if (!string.IsNullOrWhiteSpace(resolution))
            form.Add("resolution".NamedField(resolution));

        var aspect = ResolveAspectRatio(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(aspect))
            form.Add("aspect_ratio".NamedField(aspect));

        AddCommonIdeogramOptions(form, request, metadata, warnings, includeStyleSettings: true, includeColorPalette: true);

        AddReferenceImages(form, metadata, warnings);

        return form;
    }

    private static MultipartFormDataContent BuildGenerateTransparentForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var form = new MultipartFormDataContent
        {
            "prompt".NamedField(request.Prompt)
        };
        AddSeed(request, form);

        var aspect = ResolveAspectRatio(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(aspect))
            form.Add("aspect_ratio".NamedField(aspect));

        if (!string.IsNullOrWhiteSpace(metadata?.UpscaleFactor))
            form.Add("upscale_factor".NamedField(metadata.UpscaleFactor));

        AddCommonIdeogramOptions(form, request, metadata, warnings, includeStyleSettings: false, includeColorPalette: false);

        return form;
    }

    private static MultipartFormDataContent BuildEditForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Edit requires at least one source image.", nameof(request));

        var form = new MultipartFormDataContent
        {
            "prompt".NamedField(request.Prompt)
        };
        AddSeed(request, form);

        var resolution = ResolveResolution(request, metadata, warnings, allowResolution: true);
        if (!string.IsNullOrWhiteSpace(resolution))
            form.Add("resolution".NamedField(resolution));

        var aspect = ResolveAspectRatio(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(aspect))
            form.Add("aspect_ratio".NamedField(aspect));

        if (!string.IsNullOrWhiteSpace(metadata?.MagicPrompt))
            form.Add("magic_prompt".NamedField(metadata.MagicPrompt));

        var numImages = metadata?.NumImages ?? request.N;
        if (numImages is not null)
            form.Add("num_images".NamedField(numImages.Value.ToString(CultureInfo.InvariantCulture)));

        if (metadata?.TransparentBackground is not null)
        {
            form.Add("transparent_background".NamedField(
                metadata.TransparentBackground.Value.ToString().ToLowerInvariant()));
        }

        foreach (var file in files)
            form.Add(CreateImageContent(file), "images", "image");

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Ideogram v1/edit does not accept masks; the mask selected the edit route but was not sent."
            });
        }

        return form;
    }

    private static MultipartFormDataContent BuildGenerateV4Form(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent();
        if (metadata?.JsonPrompt is { } jsonPrompt
            && jsonPrompt.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            form.Add("json_prompt".NamedField(jsonPrompt.GetRawText()));
            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "prompt",
                    details = "Ignored text prompt because providerOptions.json_prompt is mutually exclusive with text_prompt."
                });
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required unless providerOptions.json_prompt is provided.", nameof(request));

            form.Add("text_prompt".NamedField(request.Prompt));
        }

        AddV4Options(form, request, metadata, warnings, includeImageWeight: false);
        return form;
    }

    private static MultipartFormDataContent BuildRemixV4Form(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required for Ideogram 4.0 remix.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Remix requires input image in files[0].", nameof(request));

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Ideogram 4.0 remix supports one input image; used files[0]."
            });
        }

        var form = new MultipartFormDataContent
        {
            "text_prompt".NamedField(request.Prompt),
            { CreateImageContent(files[0]), "image", "image" }
        };

        AddV4Options(form, request, metadata, warnings, includeImageWeight: true);
        return form;
    }

    private static void AddV4Options(
        MultipartFormDataContent form,
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings,
        bool includeImageWeight)
    {
        var resolution = ResolveResolution(request, metadata, warnings, allowResolution: true);
        if (!string.IsNullOrWhiteSpace(resolution))
            form.Add("resolution".NamedField(resolution));

        if (!string.IsNullOrWhiteSpace(metadata?.RenderingSpeed))
            form.Add("rendering_speed".NamedField(metadata.RenderingSpeed));

        if (metadata?.EnableCopyrightDetection is not null)
        {
            form.Add("enable_copyright_detection".NamedField(
                metadata.EnableCopyrightDetection.Value.ToString().ToLowerInvariant()));
        }

        if (includeImageWeight && metadata?.ImageWeight is not null)
            form.Add("image_weight".NamedField(metadata.ImageWeight.Value.ToString(CultureInfo.InvariantCulture)));

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Ideogram 4.0 does not accept a seed in the request." });
        if (request.N is not null || metadata?.NumImages is not null)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Ideogram 4.0 does not expose a number-of-images request parameter." });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) || !string.IsNullOrWhiteSpace(metadata?.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Ideogram 4.0 requires a supported resolution rather than an aspect ratio." });
    }

    private static MultipartFormDataContent BuildRemixForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Remix requires input image in files[0].", nameof(request));

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images not supported for remix; used files[0]."
            });
        }

        var form = new MultipartFormDataContent
        {
            "prompt".NamedField(request.Prompt),
            { CreateImageContent(files[0]), "image", "image" }
        };

        if (metadata?.ImageWeight is not null)
            form.Add("image_weight".NamedField(metadata.ImageWeight.Value.ToString(CultureInfo.InvariantCulture)));

        AddSeed(request, form);

        var resolution = ResolveResolution(request, metadata, warnings, allowResolution: true);
        if (!string.IsNullOrWhiteSpace(resolution))
            form.Add("resolution".NamedField(resolution));

        var aspect = ResolveAspectRatio(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(aspect))
            form.Add("aspect_ratio".NamedField(aspect));

        AddCommonIdeogramOptions(form, request, metadata, warnings, includeStyleSettings: true, includeColorPalette: true);

        AddReferenceImages(form, metadata, warnings);

        return form;
    }

    private static MultipartFormDataContent BuildReframeForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Reframe requires input image in files[0].", nameof(request));

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images not supported for reframe; used files[0]."
            });
        }

        var resolution = ResolveResolution(request, metadata, warnings, allowResolution: true);
        if (string.IsNullOrWhiteSpace(resolution))
            throw new ArgumentException("Reframe requires a resolution (use size or providerOptions.resolution).", nameof(request));

        var form = new MultipartFormDataContent
        {
            { CreateImageContent(files[0]), "image", "image" },
            "resolution".NamedField(resolution)
        };

        AddSeed(request, form);
        AddCommonIdeogramOptions(form, request, metadata, warnings, includeStyleSettings: false, includeColorPalette: true);

        AddReferenceImages(form, metadata, warnings);

        return form;
    }

    private static MultipartFormDataContent BuildReplaceBackgroundForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Replace background requires input image in files[0].", nameof(request));

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images not supported for replace-background; used files[0]."
            });
        }

        var form = new MultipartFormDataContent
        {
            "prompt".NamedField(request.Prompt),
            { CreateImageContent(files[0]), "image", "image" }
        };

        AddSeed(request, form);
        AddCommonIdeogramOptions(form, request, metadata, warnings, includeStyleSettings: false, includeColorPalette: true);

        AddReferenceImages(form, metadata, warnings);

        return form;
    }

    private static MultipartFormDataContent BuildUpscaleForm(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Upscale requires input image in files[0].", nameof(request));

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images not supported for upscale; used files[0]."
            });
        }

        var form = new MultipartFormDataContent();
        var imageRequest = BuildUpscaleRequestPayload(request, metadata);
        var json = JsonSerializer.Serialize(imageRequest, JsonSerializerOptions.Web);
        form.Add("image_request".NamedField(json));
        form.Add(CreateImageContent(files[0]), "image_file", "image_file");

        return form;
    }

    private static object BuildUpscaleRequestPayload(ImageRequest request, IdeogramImageProviderMetadata? metadata)
    {
        if (metadata?.Upscale is null && string.IsNullOrWhiteSpace(request.Prompt))
            return new { };

        return new
        {
            prompt = metadata?.Upscale?.Prompt ?? request.Prompt,
            resemblance = metadata?.Upscale?.Resemblance,
            detail = metadata?.Upscale?.Detail,
            magic_prompt_option = metadata?.Upscale?.MagicPromptOption,
            num_images = metadata?.Upscale?.NumImages ?? request.N,
            seed = metadata?.Upscale?.Seed ?? request.Seed
        };
    }

    private static void AddCommonIdeogramOptions(
        MultipartFormDataContent form,
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings,
        bool includeStyleSettings,
        bool includeColorPalette)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.RenderingSpeed))
            form.Add("rendering_speed".NamedField(metadata.RenderingSpeed));

        if (!string.IsNullOrWhiteSpace(metadata?.MagicPrompt))
            form.Add("magic_prompt".NamedField(metadata.MagicPrompt));

        if (!string.IsNullOrWhiteSpace(metadata?.NegativePrompt))
            form.Add("negative_prompt".NamedField(metadata.NegativePrompt));

        var numImages = metadata?.NumImages ?? request.N;
        if (numImages is not null)
            form.Add("num_images".NamedField(numImages.Value.ToString(CultureInfo.InvariantCulture)));

        if (includeColorPalette && metadata?.ColorPalette is { } palette && palette.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            form.Add("color_palette".NamedField(palette.ToString()));
        }

        if (includeStyleSettings)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.StyleType))
                form.Add("style_type".NamedField(metadata.StyleType));

            if (!string.IsNullOrWhiteSpace(metadata?.StylePreset))
                form.Add("style_preset".NamedField(metadata.StylePreset));

            if (metadata?.StyleCodes is { Count: > 0 } styleCodes)
            {
                foreach (var code in styleCodes.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    form.Add("style_codes".NamedField(code));
                }
            }
        }
    }

    private static void AddSeed(ImageRequest request, MultipartFormDataContent form)
    {
        if (request.Seed is not null)
            form.Add("seed".NamedField(request.Seed.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static void AddReferenceImages(
        MultipartFormDataContent form,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        AddImageList(form, "style_reference_images", metadata?.StyleReferenceImages, warnings);
        AddImageList(form, "character_reference_images", metadata?.CharacterReferenceImages, warnings);
        AddImageList(form, "character_reference_images_mask", metadata?.CharacterReferenceImagesMask, warnings);
    }

    private static void AddImageList(
        MultipartFormDataContent form,
        string fieldName,
        IReadOnlyList<string>? images,
        List<object> warnings)
    {
        if (images is null || images.Count == 0)
            return;

        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image))
                continue;

            if (!TryParseBase64(image, out var bytes, out var mimeType))
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = fieldName,
                    details = "Reference images must be base64 or data URLs."
                });
                continue;
            }

            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? MediaTypeNames.Application.Octet : mimeType);
            form.Add(content, fieldName, fieldName);
        }
    }

    private static string? ResolveResolution(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings,
        bool allowResolution)
    {
        if (!allowResolution)
            return null;

        if (!string.IsNullOrWhiteSpace(metadata?.Resolution))
            return metadata.Resolution;

        if (!string.IsNullOrWhiteSpace(request.Size))
            return request.Size;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "Aspect ratio provided without resolution; no default resolution available."
            });
        }

        return null;
    }

    private static string? ResolveAspectRatio(
        ImageRequest request,
        IdeogramImageProviderMetadata? metadata,
        List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.AspectRatio))
            return metadata.AspectRatio;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var ratio = request.AspectRatio.Replace(':', 'x');
            return ratio;
        }

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = "Size provided without aspect_ratio; use providerOptions.aspect_ratio or providerOptions.resolution instead."
            });
        }

        return null;
    }

    private static ByteArrayContent CreateImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(RemoveDataUrlPrefix(file.Data));
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Application.Octet
                : file.MediaType);

        return content;
    }

    private static bool TryParseBase64(string value, out byte[] bytes, out string? mimeType)
    {
        bytes = [];
        mimeType = null;

        if (MediaContentHelpers.TryParseDataUrl(value, out var parsedMime, out var parsedBase64))
        {
            mimeType = parsedMime;
            bytes = Convert.FromBase64String(parsedBase64);
            return true;
        }

        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ImageResponse> SendV3FormAsync(
        ImageRequest request,
        List<object> warnings,
        string endpoint,
        MultipartFormDataContent form,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        using var resp = await _client.PostAsync(endpoint, form, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var images = await ParseIdeogramImagesAsync(doc.RootElement, ct);

        if (images.Count == 0)
            throw new Exception("Ideogram response returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()) 
            }
        };
    }

    private async Task<ImageResponse> SendUpscaleFormAsync(
        ImageRequest request,
        List<object> warnings,
        MultipartFormDataContent form,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        using var resp = await _client.PostAsync("upscale", form, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var images = await ParseIdeogramImagesAsync(doc.RootElement, ct);

        if (images.Count == 0)
            throw new Exception("Ideogram upscale response returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()) 
            }
        };
    }

    private async Task<List<string>> ParseIdeogramImagesAsync(JsonElement root, CancellationToken ct)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var bytes = await _client.GetByteArrayAsync(url, ct);
            var mime = GuessImageMimeType(url);
            images.Add(ToDataUrl(Convert.ToBase64String(bytes), mime));
        }

        return images;
    }

    private static string GuessImageMimeType(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        return MediaTypeNames.Image.Png;
    }

    private static string RemoveDataUrlPrefix(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var commaIndex = input.IndexOf(',');
        if (!input.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || commaIndex < 0)
            return input;

        return input[(commaIndex + 1)..];
    }

    private static string ToDataUrl(string base64, string mimeType)
        => $"data:{mimeType};base64,{base64}";
}
