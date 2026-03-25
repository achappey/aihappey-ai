using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Lumenfall;

public partial class LumenfallProvider
{
    private static readonly JsonSerializerOptions LumenfallImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> LumenfallGenerationMetadataFields =
    [
        "quality",
        "response_format",
        "output_format",
        "output_compression",
        "style",
        "user",
        "dryRun"
    ];

    private static readonly HashSet<string> LumenfallEditMetadataFields =
    [
        "quality",
        "response_format",
        "output_format",
        "output_compression",
        "user",
        "dryRun"
    ];

    private async Task<ImageResponse> ImageRequestLumenfall(ImageRequest request, CancellationToken cancellationToken = default)
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
        var files = request.Files?.ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "Lumenfall image endpoints use size. aspectRatio is not documented and was ignored."
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "Lumenfall image endpoints do not document seed."
            });
        }

        var requestedOutputFormat = LumenfallTryGetString(metadata, "output_format");
        var dryRun = LumenfallTryGetBool(metadata, "dryRun") == true;

        HttpResponseMessage httpResponse;
        string endpoint;

        if (files.Count == 0)
        {
            LumenfallWarnUnsupportedMetadata(metadata, LumenfallGenerationMetadataFields, "v1/images/generations", warnings);

            if (request.Mask is not null)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "mask",
                    details = "Mask is only documented on /v1/images/edits and was ignored."
                });
            }

            var payload = BuildGenerationPayload(request, metadata, warnings);
            endpoint = dryRun ? "v1/images/generations?dryRun=true" : "v1/images/generations";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, LumenfallImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            httpResponse = await _client.SendAsync(httpRequest, cancellationToken);
        }
        else
        {
            LumenfallWarnUnsupportedMetadata(metadata, LumenfallEditMetadataFields, "v1/images/edits", warnings);

            if (LumenfallHasProperty(metadata, "style"))
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "providerOptions.style",
                    details = "style is documented for generation and was ignored on /v1/images/edits."
                });
            }

            endpoint = dryRun ? "v1/images/edits?dryRun=true" : "v1/images/edits";
            using var form = BuildEditForm(request, files, metadata, warnings);
            httpResponse = await _client.PostAsync(endpoint, form, cancellationToken);
        }

        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lumenfall image request failed ({(int)httpResponse.StatusCode}) [{endpoint}]: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ParseImagesAsync(root, requestedOutputFormat, cancellationToken);
        if (!dryRun && images.Count == 0)
            throw new InvalidOperationException("Lumenfall image response did not contain images.");

        var usage = ParseUsage(root);
        var timestamp = ResolveTimestamp(root, now);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
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
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static Dictionary<string, object?> BuildGenerationPayload(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = NormalizeN(request.N, warnings),
            ["size"] = request.Size,
            ["quality"] = LumenfallTryGetString(metadata, "quality"),
            ["response_format"] = LumenfallTryGetString(metadata, "response_format"),
            ["output_format"] = LumenfallTryGetString(metadata, "output_format"),
            ["output_compression"] = LumenfallTryGetInt(metadata, "output_compression"),
            ["style"] = LumenfallTryGetString(metadata, "style"),
            ["user"] = LumenfallTryGetString(metadata, "user")
        };

        return payload;
    }

    private static MultipartFormDataContent BuildEditForm(
        ImageRequest request,
        IReadOnlyList<ImageFile> files,
        JsonElement metadata,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent();

        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent(request.Prompt), "prompt");

        var n = NormalizeN(request.N, warnings);
        if (n.HasValue)
            form.Add(new StringContent(n.Value.ToString()), "n");

        AddOptionalString(form, "size", request.Size);
        AddOptionalString(form, "quality", LumenfallTryGetString(metadata, "quality"));
        AddOptionalString(form, "response_format", LumenfallTryGetString(metadata, "response_format"));
        AddOptionalString(form, "output_format", LumenfallTryGetString(metadata, "output_format"));
        AddOptionalString(form, "user", LumenfallTryGetString(metadata, "user"));

        var outputCompression = LumenfallTryGetInt(metadata, "output_compression");
        if (outputCompression.HasValue)
            form.Add(new StringContent(outputCompression.Value.ToString()), "output_compression");

        var imageField = files.Count > 1 ? "image[]" : "image";
        for (var i = 0; i < files.Count; i++)
            form.Add(CreateImageContent(files[i]), imageField, $"image-{i + 1}{GetImageExtension(files[i].MediaType)}");

        if (request.Mask is not null)
            form.Add(CreateImageContent(request.Mask), "mask", $"mask{GetImageExtension(request.Mask.MediaType)}");

        return form;
    }

    private async Task<List<string>> ParseImagesAsync(JsonElement root, string? requestedOutputFormat, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        var fallbackMediaType = ResolveOutputMediaType(requestedOutputFormat);

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(fallbackMediaType));

                continue;
            }

            if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                using var imageResponse = await _client.GetAsync(url, cancellationToken);
                var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                if (!imageResponse.IsSuccessStatusCode || bytes.Length == 0)
                    continue;

                var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    mediaType = fallbackMediaType;

                images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
            }
        }

        return images;
    }

    private static ImageUsageData? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        var inputTokens = LumenfallTryGetInt(usageEl, "prompt_tokens") ?? LumenfallTryGetInt(usageEl, "input_tokens");
        var outputTokens = LumenfallTryGetInt(usageEl, "completion_tokens") ?? LumenfallTryGetInt(usageEl, "output_tokens");
        var totalTokens = LumenfallTryGetInt(usageEl, "total_tokens");

        if (inputTokens is null && outputTokens is null && totalTokens is null)
            return null;

        return new ImageUsageData
        {
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens
        };
    }

    private static DateTime ResolveTimestamp(JsonElement root, DateTime fallbackUtc)
    {
        if (root.TryGetProperty("created", out var createdEl) &&
            createdEl.ValueKind == JsonValueKind.Number &&
            createdEl.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch
            {
                return fallbackUtc;
            }
        }

        return fallbackUtc;
    }

    private static int? NormalizeN(int? value, List<object> warnings)
    {
        if (!value.HasValue)
            return null;

        var clamped = Math.Clamp(value.Value, 1, 10);
        if (clamped != value.Value)
        {
            warnings.Add(new
            {
                type = "clamped",
                feature = "n",
                details = "Lumenfall image n is documented as 1..10. Value was clamped."
            });
        }

        return clamped;
    }

    private static void LumenfallWarnUnsupportedMetadata(
        JsonElement metadata,
        HashSet<string> supportedFields,
        string endpoint,
        List<object> warnings)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in metadata.EnumerateObject())
        {
            if (!supportedFields.Contains(prop.Name))
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = $"providerOptions.{prop.Name}",
                    details = $"Field is not documented for Lumenfall endpoint '{endpoint}' and was ignored."
                });
            }
        }
    }

    private static ByteArrayContent CreateImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Application.Octet
                : file.MediaType);

        return content;
    }

    private static string GetImageExtension(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return ".bin";

        return mediaType.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Png => ".png",
            MediaTypeNames.Image.Jpeg => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            "image/avif" => ".avif",
            _ => ".bin"
        };
    }

    private static string ResolveOutputMediaType(string? outputFormat)
    {
        return outputFormat?.Trim().ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "jpeg" => MediaTypeNames.Image.Jpeg,
            "jpg" => MediaTypeNames.Image.Jpeg,
            "gif" => MediaTypeNames.Image.Gif,
            "webp" => "image/webp",
            "avif" => "image/avif",
            _ => MediaTypeNames.Image.Png
        };
    }

    private static void AddOptionalString(MultipartFormDataContent form, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), field);
    }

    private static bool LumenfallHasProperty(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return false;

        return metadata.TryGetProperty(propertyName, out _);
    }

    private static string? LumenfallTryGetString(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? LumenfallTryGetInt(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            return n;

        return null;
    }

    private static bool? LumenfallTryGetBool(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(propertyName, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null
        };
    }
}
