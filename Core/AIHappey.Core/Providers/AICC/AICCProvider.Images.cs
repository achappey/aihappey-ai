using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AICC;

public partial class AICCProvider
{
    private static readonly JsonSerializerOptions AiccImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestAICC(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var family = ResolveImageFamily(request.Model, request.Files?.Any() == true, metadata);

        HttpResponseMessage response;
        string endpoint;

        switch (family)
        {
            case "openai":
            {
                var hasFiles = request.Files?.Any() == true;
                endpoint = hasFiles ? "v1/images/edits" : "v1/images/generations";
                response = hasFiles
                    ? await SendOpenAiEditAsync(request, metadata, warnings, cancellationToken)
                    : await SendOpenAiGenerationAsync(request, metadata, warnings, cancellationToken);
                break;
            }
            case "qwen":
            {
                var hasFiles = request.Files?.Any() == true;
                endpoint = hasFiles ? "v1/images/edits" : "v1/images/generations";
                response = hasFiles
                    ? await SendQwenEditAsync(request, metadata, warnings, cancellationToken)
                    : await SendQwenGenerationAsync(request, metadata, warnings, cancellationToken);
                break;
            }
            case "gemini":
            {
                endpoint = "v1beta/models/{model}:generateContent";
                response = await SendGeminiGenerationAsync(request, metadata, warnings, cancellationToken);
                break;
            }
            case "volcengine":
            {
                endpoint = "v1/images/generations";
                response = await SendVolcengineGenerationAsync(request, metadata, warnings, cancellationToken);
                break;
            }
            default:
                throw new NotSupportedException($"AICC image family '{family}' is not supported.");
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AICC image request failed ({(int)response.StatusCode}) [{family}/{endpoint}]: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ExtractImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException($"AICC image response did not contain images for family '{family}'.");

        var usage = ExtractUsage(root, family);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                family,
                endpoint,
                body = root.Clone()
            }, JsonSerializerOptions.Web)
        };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = usage,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = ResolveTimestamp(root, now),
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private async Task<HttpResponseMessage> SendOpenAiGenerationAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = request.Size,
            ["response_format"] = TryGetString(metadata, "response_format") ?? "b64_json",
            ["quality"] = TryGetString(metadata, "quality"),
            ["output_format"] = TryGetString(metadata, "output_format"),
            ["moderation"] = TryGetString(metadata, "moderation"),
            ["partial_images"] = TryGetInt(metadata, "partial_images")
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AiccImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOpenAiEditAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent(request.Prompt), "prompt");

        if (request.N.HasValue)
            form.Add(new StringContent(request.N.Value.ToString()), "n");

        if (!string.IsNullOrWhiteSpace(request.Size))
            form.Add(new StringContent(request.Size), "size");

        AddOptionalString(form, "quality", TryGetString(metadata, "quality"));
        AddOptionalString(form, "output_format", TryGetString(metadata, "output_format"));
        AddOptionalString(form, "response_format", TryGetString(metadata, "response_format") ?? "b64_json");
        AddOptionalString(form, "moderation", TryGetString(metadata, "moderation"));
        AddOptionalString(form, "background", TryGetString(metadata, "background"));
        AddOptionalString(form, "input_fidelity", TryGetString(metadata, "input_fidelity"));

        var partialImages = TryGetInt(metadata, "partial_images");
        if (partialImages.HasValue)
            form.Add(new StringContent(partialImages.Value.ToString()), "partial_images");

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("At least one file is required for OpenAI image edits.", nameof(request));

        for (var i = 0; i < files.Count; i++)
        {
            var content = await CreateImageContentAsync(files[i], cancellationToken);
            form.Add(content, "image", $"image-{i + 1}{GetImageExtension(files[i].MediaType)}");
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        return await _client.PostAsync("v1/images/edits", form, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendGeminiGenerationAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Gemini image route returns one image candidate set." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var modelPath = request.Model;

        var parts = new List<object>
        {
            new { text = request.Prompt }
        };

        if (request.Files?.Any() == true)
        {
            foreach (var file in request.Files)
            {
                var payload = file.Data.RemoveDataUrlPrefix();
                var mimeType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
                parts.Add(new
                {
                    inlineData = new
                    {
                        mimeType,
                        data = payload
                    }
                });
            }
        }

        var responseModalities = TryGetStringArray(metadata, "responseModalities") ?? ["TEXT", "IMAGE"];

        var payloadObj = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts
                }
            },
            generationConfig = new
            {
                responseModalities,
                imageConfig = new
                {
                    aspectRatio = request.AspectRatio ?? TryGetString(metadata, "aspectRatio"),
                    imageSize = request.Size ?? TryGetString(metadata, "imageSize")
                }
            }
        };

        var route = $"v1beta/models/{modelPath}:generateContent";

        using var req = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadObj, AiccImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendVolcengineGenerationAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Volcengine route does not expose n in this adapter." });

        object? imageInput = null;
        if (request.Files?.Any() == true)
        {
            var inputs = request.Files.Select(ToImageInputString).Where(static s => !string.IsNullOrWhiteSpace(s)).ToList();
            imageInput = inputs.Count switch
            {
                0 => null,
                1 => inputs[0],
                _ => inputs
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["image"] = imageInput,
            ["size"] = request.Size ?? TryGetString(metadata, "size"),
            ["seed"] = request.Seed ?? TryGetInt(metadata, "seed"),
            ["response_format"] = TryGetString(metadata, "response_format") ?? "b64_json",
            ["watermark"] = TryGetBool(metadata, "watermark")
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AiccImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendQwenGenerationAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Qwen generation route ignores files; use edit route by providing files." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Qwen generation route does not expose n in this adapter." });

        var payload = new
        {
            model = request.Model,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new[]
                        {
                            new { text = request.Prompt }
                        }
                    }
                }
            },
            parameters = new
            {
                negative_prompt = TryGetString(metadata, "negative_prompt"),
                prompt_extend = TryGetBool(metadata, "prompt_extend"),
                watermark = TryGetBool(metadata, "watermark"),
                size = request.Size ?? TryGetString(metadata, "size")
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AiccImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendQwenEditAsync(
        ImageRequest request,
        JsonElement metadata,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("At least one file is required for Qwen image edits.", nameof(request));

        var contentParts = new List<object>();
        foreach (var file in files)
            contentParts.Add(new { image = ToImageInputString(file) });

        contentParts.Add(new { text = request.Prompt });

        var payload = new
        {
            model = request.Model,
            input = new
            {
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = contentParts
                    }
                }
            },
            parameters = new
            {
                n = request.N,
                negative_prompt = TryGetString(metadata, "negative_prompt"),
                prompt_extend = TryGetBool(metadata, "prompt_extend"),
                watermark = TryGetBool(metadata, "watermark"),
                size = request.Size ?? TryGetString(metadata, "size")
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/edits")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AiccImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, cancellationToken);
    }

    private async Task<List<string>> ExtractImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<string> images = [];

        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
                {
                    var b64 = b64El.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                        images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));

                    continue;
                }

                if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    var url = urlEl.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var dataUrl = await DownloadAsDataUrlAsync(url, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(dataUrl))
                            images.Add(dataUrl);
                    }
                }
            }
        }

        CollectGeminiImages(root, images);

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private static void CollectGeminiImages(JsonElement element, List<string> images)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (TryGetInlineDataUrl(element, out var inlineDataUrl))
                    images.Add(inlineDataUrl);

                foreach (var property in element.EnumerateObject())
                    CollectGeminiImages(property.Value, images);

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                    CollectGeminiImages(item, images);

                break;
            }
        }
    }

    private static bool TryGetInlineDataUrl(JsonElement obj, out string dataUrl)
    {
        dataUrl = string.Empty;

        JsonElement dataEl;
        if (!TryGetPropertyIgnoreCase(obj, "data", out dataEl) || dataEl.ValueKind != JsonValueKind.String)
            return false;

        var b64 = dataEl.GetString();
        if (string.IsNullOrWhiteSpace(b64))
            return false;

        var mimeType = MediaTypeNames.Image.Png;

        if (TryGetPropertyIgnoreCase(obj, "mimeType", out var mimeEl) && mimeEl.ValueKind == JsonValueKind.String)
        {
            var mt = mimeEl.GetString();
            if (!string.IsNullOrWhiteSpace(mt) && mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mimeType = mt;
        }
        else if (TryGetPropertyIgnoreCase(obj, "mime_type", out var snakeMimeEl) && snakeMimeEl.ValueKind == JsonValueKind.String)
        {
            var mt = snakeMimeEl.GetString();
            if (!string.IsNullOrWhiteSpace(mt) && mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mimeType = mt;
        }

        dataUrl = b64.ToDataUrl(mimeType);
        return true;
    }

    private static ImageUsageData? ExtractUsage(JsonElement root, string family)
    {
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            return new ImageUsageData
            {
                InputTokens = TryGetInt(usageEl, "prompt_tokens"),
                OutputTokens = TryGetInt(usageEl, "completion_tokens"),
                TotalTokens = TryGetInt(usageEl, "total_tokens")
            };
        }

        if (string.Equals(family, "gemini", StringComparison.OrdinalIgnoreCase)
            && root.TryGetProperty("usageMetadata", out var usageMetadata)
            && usageMetadata.ValueKind == JsonValueKind.Object)
        {
            var input = TryGetInt(usageMetadata, "promptTokenCount");
            var output = TryGetInt(usageMetadata, "candidatesTokenCount");
            var total = TryGetInt(usageMetadata, "totalTokenCount");

            if (input.HasValue || output.HasValue || total.HasValue)
            {
                return new ImageUsageData
                {
                    InputTokens = input,
                    OutputTokens = output,
                    TotalTokens = total
                };
            }
        }

        return null;
    }

    private static DateTime ResolveTimestamp(JsonElement root, DateTime fallbackUtc)
    {
        if (root.TryGetProperty("created", out var createdEl)
            && createdEl.ValueKind == JsonValueKind.Number
            && createdEl.TryGetInt64(out var unix))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            }
            catch
            {
                return fallbackUtc;
            }
        }

        return fallbackUtc;
    }

    private async Task<string?> DownloadAsDataUrlAsync(string imageUrl, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(imageUrl, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode || bytes is null || bytes.Length == 0)
            return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessImageMediaType(imageUrl)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private async Task<ByteArrayContent> CreateImageContentAsync(ImageFile file, CancellationToken cancellationToken)
    {
        var payload = file.Data ?? string.Empty;
        byte[] bytes;

        if (payload.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            bytes = await _client.GetByteArrayAsync(payload, cancellationToken);
        }
        else
        {
            var base64 = payload.RemoveDataUrlPrefix();
            bytes = Convert.FromBase64String(base64);
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Application.Octet
            : file.MediaType;

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static string ResolveImageFamily(string model, bool hasFiles, JsonElement metadata)
    {
        var explicitFamily = TryGetString(metadata, "family");
        if (!string.IsNullOrWhiteSpace(explicitFamily))
            return explicitFamily.Trim().ToLowerInvariant();

        var normalized = model.ToLowerInvariant();

        if (normalized.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return "gemini";

        if (normalized.Contains("seedream", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("doubao", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("volc", StringComparison.OrdinalIgnoreCase))
        {
            return "volcengine";
        }

        if (normalized.Contains("qwen", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("wan", StringComparison.OrdinalIgnoreCase))
        {
            return "qwen";
        }

        if (hasFiles)
            return "openai";

        return "openai";
    }

    private static string ToImageInputString(ImageFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Data)
            && (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
        {
            return file.Data;
        }

        var base64 = (file.Data ?? string.Empty).RemoveDataUrlPrefix();
        if (string.IsNullOrWhiteSpace(base64))
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(file.MediaType))
            return $"data:{file.MediaType};base64,{base64}";

        return base64;
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (url.Contains(".gif", StringComparison.OrdinalIgnoreCase))
            return "image/gif";
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        return null;
    }

    private static string GetImageExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg"
        };
    }

    private static void AddOptionalString(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), name);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        return el.GetString();
    }

    private static int? TryGetInt(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            return n;

        return null;
    }

    private static bool? TryGetBool(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var el))
            return null;

        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();

        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b))
            return b;

        return null;
    }

    private static string[]? TryGetStringArray(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        var arr = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                arr.Add(value);
        }

        return arr.Count == 0 ? null : [.. arr];
    }
}
