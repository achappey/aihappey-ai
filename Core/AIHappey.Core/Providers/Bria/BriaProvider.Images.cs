using AIHappey.Common.Model;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Bria;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Bria;

public partial class BriaProvider : IModelProvider
{
    private static readonly JsonSerializerOptions BriaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string NormalizeModelToEndpoint(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Trim();

        return "image/" + m;
    }

    private static string? GetSingleImageInputDataUrl(ImageRequest imageRequest)
        => imageRequest.Files?.FirstOrDefault() is { } f
            ? f.ToDataUrl()
            : null;

    private static string? GetMaskInputDataUrl(ImageRequest imageRequest)
        => imageRequest.Mask?.ToDataUrl();

    private async Task<BriaResultEnvelope> PostBriaAsync<T>(
        string endpoint,
        T payload,
        CancellationToken ct)
    {
        ApplyAuthHeader();

        var json = JsonSerializer.Serialize(payload, BriaJson);
        using var resp = await _client.PostAsync(
            endpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            ct);

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var env = JsonSerializer.Deserialize<BriaResultEnvelope>(raw, BriaJson)
            ?? throw new Exception("Bria returned an empty response.");

        static bool IsTerminalStatus(string? status)
            => string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "UNKNOWN", StringComparison.OrdinalIgnoreCase);

        static bool IsInProgressStatus(string? status)
            => string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase);

        static bool IsCompletedStatus(string? status)
            => string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase);

        static bool IsErrorStatus(string? status)
            => string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "UNKNOWN", StringComparison.OrdinalIgnoreCase);

        static string BuildStatusErrorMessage(BriaResultEnvelope current)
        {
            var req = string.IsNullOrWhiteSpace(current.RequestId) ? "" : $" request_id={current.RequestId}.";
            var status = string.IsNullOrWhiteSpace(current.Status) ? "<missing>" : current.Status;
            var msg = current.Error?.Message;
            var type = current.Error?.Type;
            var code = current.Error?.Code;
            var details = current.Error?.Details;

            var extra = new List<string>();
            if (!string.IsNullOrWhiteSpace(type)) extra.Add($"type={type}");
            if (!string.IsNullOrWhiteSpace(code)) extra.Add($"code={code}");
            if (!string.IsNullOrWhiteSpace(msg)) extra.Add($"message={msg}");
            if (!string.IsNullOrWhiteSpace(details)) extra.Add($"details={details}");

            var extraText = extra.Count == 0 ? "" : " " + string.Join(", ", extra);
            return $"Bria async request ended with status '{status}'.{req}{extraText}";
        }

        // Async workflow (Bria v2): initial response may include status_url OR may be a status payload
        // ({ status: IN_PROGRESS, request_id }) depending on endpoint/flags.
        // Poll status_url until terminal state.
        if (env.Result is null && (IsInProgressStatus(env.Status) || !string.IsNullOrWhiteSpace(env.StatusUrl)))
        {
            if (string.IsNullOrWhiteSpace(env.StatusUrl))
                throw new Exception("Bria returned an async status without status_url; cannot poll.");

            var statusUrl = env.StatusUrl;

            while (!IsTerminalStatus(env.Status) && env.Result is null)
            {
                ct.ThrowIfCancellationRequested();

                // status_url is fully qualified per docs; just GET it.
                using var pollResp = await _client.GetAsync(statusUrl, ct);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);
                if (!pollResp.IsSuccessStatusCode)
                    throw new Exception($"{pollResp.StatusCode}: {pollRaw}");

                env = JsonSerializer.Deserialize<BriaResultEnvelope>(pollRaw, BriaJson)
                    ?? throw new Exception("Bria returned an empty poll response.");

                if (IsErrorStatus(env.Status))
                    throw new Exception(BuildStatusErrorMessage(env));

                if (env.Result is not null || IsCompletedStatus(env.Status))
                    break;

                // Polling interval: fixed 2s (no hard timeout; only CancellationToken can stop it)
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            if (IsErrorStatus(env.Status))
                throw new Exception(BuildStatusErrorMessage(env));
        }

        return env;
    }

    private async Task<(string dataUrl, string mimeType)> DownloadImageAsDataUrlAsync(string imageUrl, CancellationToken ct)
    {
        using var resp = await _client.GetAsync(imageUrl, ct);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Image.Png;
        var b64 = Convert.ToBase64String(bytes);
        return (b64.ToDataUrl(mediaType), mediaType);
    }

    private static Dictionary<string, JsonElement> ToProviderMetadata(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, BriaJson);
        return new Dictionary<string, JsonElement>
        {
            ["bria"] = element
        };
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);

        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        // Bria endpoints accept images as public URL or base64/data-url.
        // We use `data:<mime>;base64,...` to be consistent with other providers.
        var endpoint = NormalizeModelToEndpoint(imageRequest.Model);
        var now = DateTime.UtcNow;

        // Provider metadata root: { bria: { generateImage: {...}, imageEdit: {...}, ... } }
        var briaOptions = imageRequest.GetImageProviderMetadata<BriaImageProviderMetadata>(GetIdentifier());

        BriaResultEnvelope env;

        switch (endpoint)
        {
            case "image/generate":
            case "image/generate/lite":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest);
                    var payload = new BriaGenerateRequest
                    {
                        Prompt = imageRequest.Prompt,
                        Images = string.IsNullOrWhiteSpace(inputImage) ? null : [inputImage],
                        StructuredPrompt = briaOptions?.GenerateImage?.StructuredPrompt,
                        NegativePrompt = briaOptions?.GenerateImage?.NegativePrompt,
                        GuidanceScale = briaOptions?.GenerateImage?.GuidanceScale,
                        StepsNum = briaOptions?.GenerateImage?.StepsNum,
                        Seed = imageRequest.Seed,
                        AspectRatio = imageRequest.AspectRatio,
                        IpSignal = briaOptions?.GenerateImage?.IpSignal,
                        PromptContentModeration = briaOptions?.GenerateImage?.PromptContentModeration,
                        VisualInputContentModeration = briaOptions?.GenerateImage?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.GenerateImage?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException("Bria image/edit requires exactly one input image.");

                    var payload = new BriaEditRequest
                    {
                        Images = [inputImage],
                        Instruction = imageRequest.Prompt,
                        StructuredInstruction = briaOptions?.ImageEdit?.StructuredInstructions,
                        Mask = GetMaskInputDataUrl(imageRequest),
                        NegativePrompt = briaOptions?.ImageEdit?.NegativePrompt,
                        GuidanceScale = briaOptions?.ImageEdit?.GuidanceScale,
                        StepsNum = briaOptions?.ImageEdit?.StepsNum,
                        Seed = imageRequest.Seed,
                        IpSignal = briaOptions?.ImageEdit?.IpSignal,
                        PromptContentModeration = briaOptions?.ImageEdit?.PromptContentModeration,
                        VisualInputContentModeration = briaOptions?.ImageEdit?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.ImageEdit?.VisualOutputContentModeration,
                        Sync = true
                    };

                    // If structured_instruction is provided, Bria expects it INSTEAD of instruction.
                    if (!string.IsNullOrWhiteSpace(payload.StructuredInstruction))
                        payload.Instruction = null;

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/add_object_by_text":
            case "image/edit/replace_object_by_text":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaEditByTextRequest
                    {
                        Image = inputImage,
                        Instruction = imageRequest.Prompt
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/erase_by_text":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
                        throw new InvalidOperationException("Bria image/edit/erase_by_text requires ImageRequest.Prompt to be set (mapped to object_name).");

                    var payload = new BriaEraseByTextRequest
                    {
                        Image = inputImage,
                        ObjectName = imageRequest.Prompt
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/blend":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
                        throw new InvalidOperationException("Bria image/edit/blend requires ImageRequest.Prompt to be set (mapped to instruction).");

                    var payload = new BriaBlendRequest
                    {
                        Image = inputImage,
                        Instruction = imageRequest.Prompt
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/reseason":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var season = briaOptions?.ReseasonImage?.Season;
                    if (string.IsNullOrWhiteSpace(season))
                        season = imageRequest.Prompt;

                    if (string.IsNullOrWhiteSpace(season))
                        throw new InvalidOperationException("Bria image/edit/reseason requires a season (provide bria.reseasonImage.season provider metadata, or set ImageRequest.Prompt to the season).");

                    var payload = new BriaReseasonRequest
                    {
                        Image = inputImage,
                        Season = season
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/replace_text":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
                        throw new InvalidOperationException("Bria image/edit/replace_text requires ImageRequest.Prompt to be set (mapped to new_text).");

                    var payload = new BriaReplaceTextRequest
                    {
                        Image = inputImage,
                        NewText = imageRequest.Prompt
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/sketch_to_image":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaSketchToImageRequest
                    {
                        Image = inputImage
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/restore":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaRestoreRequest
                    {
                        Image = inputImage,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/colorize":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var style = briaOptions?.Colorize?.Style;
                    if (string.IsNullOrWhiteSpace(style))
                        style = imageRequest.Prompt;

                    if (string.IsNullOrWhiteSpace(style))
                        throw new InvalidOperationException("Bria image/edit/colorize requires a style (provide bria.colorize.style provider metadata, or set ImageRequest.Prompt to the style id).");

                    var payload = new BriaColorizeRequest
                    {
                        Image = inputImage,
                        Style = style,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/restyle":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var style = briaOptions?.RestyleImage?.Style;
                    if (string.IsNullOrWhiteSpace(style))
                        style = imageRequest.Prompt;

                    if (string.IsNullOrWhiteSpace(style))
                        throw new InvalidOperationException("Bria image/edit/restyle requires a style (provide bria.restyleImage.style provider metadata, or set ImageRequest.Prompt to the style id/free text).");

                    var payload = new BriaRestyleRequest
                    {
                        Image = inputImage,
                        Style = style,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/relight":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var lightType = briaOptions?.RelightImage?.LightType;
                    if (string.IsNullOrWhiteSpace(lightType))
                        lightType = imageRequest.Prompt;

                    if (string.IsNullOrWhiteSpace(lightType))
                        throw new InvalidOperationException("Bria image/edit/relight requires a light_type (provide bria.relightImage.light_type provider metadata, or set ImageRequest.Prompt to the light_type).");

                    var payload = new BriaRelightRequest
                    {
                        Image = inputImage,
                        LightType = lightType,
                        LightDirection = briaOptions?.RelightImage?.LightDirection,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/erase":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var mask = GetMaskInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException("Bria image/edit/erase requires ImageRequest.Mask to be provided.");

                    var payload = new BriaEraseRequest
                    {
                        Image = inputImage,
                        Mask = mask,
                        MaskType = briaOptions?.Eraser?.MaskType,
                        PreserveAlpha = briaOptions?.Eraser?.PreserveAlpha,
                        VisualInputContentModeration = briaOptions?.Eraser?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.Eraser?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/gen_fill":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var mask = GetMaskInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException("Bria image/edit/gen_fill requires ImageRequest.Mask to be provided.");

                    if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
                        throw new InvalidOperationException("Bria image/edit/gen_fill requires ImageRequest.Prompt to be set.");

                    var payload = new BriaGenFillRequest
                    {
                        Image = inputImage,
                        Mask = mask,
                        Prompt = imageRequest.Prompt,
                        Version = briaOptions?.GenerativeFill?.Version,
                        RefinePrompt = briaOptions?.GenerativeFill?.RefinePrompt,
                        TailoredModelId = briaOptions?.GenerativeFill?.TailoredModelId,
                        PromptContentModeration = briaOptions?.GenerativeFill?.PromptContentModeration,
                        NegativePrompt = briaOptions?.GenerativeFill?.NegativePrompt,
                        PreserveAlpha = briaOptions?.GenerativeFill?.PreserveAlpha,
                        Seed = imageRequest.Seed,
                        VisualInputContentModeration = briaOptions?.GenerativeFill?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.GenerativeFill?.VisualOutputContentModeration,
                        MaskType = briaOptions?.GenerativeFill?.MaskType,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/remove_background":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaRemoveBackgroundRequest
                    {
                        Image = inputImage,
                        PreserveAlpha = briaOptions?.RemoveBackground?.PreserveAlpha,
                        VisualInputContentModeration = briaOptions?.RemoveBackground?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.RemoveBackground?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/replace_background":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaReplaceBackgroundRequest
                    {
                        Image = inputImage,
                        Mode = briaOptions?.ReplaceBackground?.Mode,
                        RefImages = briaOptions?.ReplaceBackground?.RefImages,
                        EnhanceRefImages = briaOptions?.ReplaceBackground?.EnhanceRefImages,
                        Prompt = imageRequest.Prompt,
                        RefinePrompt = briaOptions?.ReplaceBackground?.RefinePrompt,
                        PromptContentModeration = briaOptions?.ReplaceBackground?.PromptContentModeration,
                        NegativePrompt = briaOptions?.ReplaceBackground?.NegativePrompt,
                        OriginalQuality = briaOptions?.ReplaceBackground?.OriginalQuality,
                        ForceBackgroundDetection = briaOptions?.ReplaceBackground?.ForceBackgroundDetection,
                        VisualOutputContentModeration = briaOptions?.ReplaceBackground?.VisualOutputContentModeration,
                        Seed = imageRequest.Seed,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/erase_foreground":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaEraseForegroundRequest
                    {
                        Image = inputImage,
                        PreserveAlpha = briaOptions?.EraseForeground?.PreserveAlpha,
                        VisualInputContentModeration = briaOptions?.EraseForeground?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.EraseForeground?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/blur_background":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaBlurBackgroundRequest
                    {
                        Image = inputImage,
                        Scale = briaOptions?.BlurBackground?.Scale,
                        PreserveAlpha = briaOptions?.BlurBackground?.PreserveAlpha,
                        VisualInputContentModeration = briaOptions?.BlurBackground?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.BlurBackground?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/expand":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaExpandImageRequest
                    {
                        Image = inputImage,
                        AspectRatio = imageRequest.AspectRatio,
                        CanvasSize = briaOptions?.ExpandImage?.CanvasSize,
                        OriginalImageSize = briaOptions?.ExpandImage?.OriginalImageSize,
                        OriginalImageLocation = briaOptions?.ExpandImage?.OriginalImageLocation,
                        Prompt = imageRequest.Prompt,
                        PromptContentModeration = briaOptions?.ExpandImage?.PromptContentModeration,
                        NegativePrompt = briaOptions?.ExpandImage?.NegativePrompt,
                        PreserveAlpha = briaOptions?.ExpandImage?.PreserveAlpha,
                        Seed = imageRequest.Seed,
                        VisualInputContentModeration = briaOptions?.ExpandImage?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.ExpandImage?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/enhance":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaEnhanceImageRequest
                    {
                        Image = inputImage,
                        Resolution = briaOptions?.EnhanceImage?.Resolution,
                        StepsNum = briaOptions?.EnhanceImage?.StepsNum,
                        PreserveAlpha = briaOptions?.EnhanceImage?.PreserveAlpha,
                        Seed = imageRequest.Seed,
                        VisualInputContentModeration = briaOptions?.EnhanceImage?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.EnhanceImage?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/increase_resolution":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaIncreaseResolutionRequest
                    {
                        Image = inputImage,
                        PreserveAlpha = briaOptions?.IncreaseResolution?.PreserveAlpha,
                        DesiredIncrease = briaOptions?.IncreaseResolution?.DesiredIncrease,
                        VisualInputContentModeration = briaOptions?.IncreaseResolution?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.IncreaseResolution?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            case "image/edit/crop_foreground":
                {
                    var inputImage = GetSingleImageInputDataUrl(imageRequest)
                        ?? throw new InvalidOperationException($"Bria {endpoint} requires exactly one input image.");

                    var payload = new BriaCropForegroundRequest
                    {
                        Image = inputImage,
                        Padding = briaOptions?.CropoutForeground?.Padding,
                        ForceBackgroundDetection = briaOptions?.CropoutForeground?.ForceBackgroundDetection,
                        PreserveAlpha = briaOptions?.CropoutForeground?.PreserveAlpha,
                        VisualInputContentModeration = briaOptions?.CropoutForeground?.VisualInputContentModeration,
                        VisualOutputContentModeration = briaOptions?.CropoutForeground?.VisualOutputContentModeration,
                        Sync = true
                    };

                    env = await PostBriaAsync(endpoint, payload, cancellationToken);
                    break;
                }

            default:
                throw new NotSupportedException($"Bria model '{imageRequest.Model}' (endpoint '{endpoint}') is not implemented yet.");
        }

        var imageUrl = env.Result?.ImageUrl;
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new Exception("Bria response did not include result.image_url.");

        var (dataUrl, _) = await DownloadImageAsDataUrlAsync(imageUrl, cancellationToken);

        var providerMeta = new
        {
            request_id = env.RequestId,
            warning = env.Warning,
            status = env.Status,
            status_url = env.StatusUrl,
            error = env.Error,
            seed = env.Result?.Seed,
            structured_prompt = env.Result?.StructuredPrompt,
            structured_instruction = env.Result?.StructuredInstruction,
            image_url = env.Result?.ImageUrl,
            endpoint
        };

        return new ImageResponse
        {
            Images = [dataUrl],
            ProviderMetadata = ToProviderMetadata(providerMeta),
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = env
            }
        };
    }


}
