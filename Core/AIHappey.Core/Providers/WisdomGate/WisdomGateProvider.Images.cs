using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WisdomGate;

public partial class WisdomGateProvider
{
    private static readonly JsonSerializerOptions WisdomGateImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> WisdomGateImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(WisdomGate)} API key.");

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var parts = new List<object>
        {
            new { text = request.Prompt }
        };

        if (request.Files?.Any() == true)
        {
            foreach (var file in request.Files)
            {
                var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
                var payload = file.Data.RemoveDataUrlPrefix();
                parts.Add(new
                {
                    inlineData = new
                    {
                        mimeType = mediaType,
                        data = payload
                    }
                });
            }
        }

        var responseModalities = WgImageTryGetStringArray(metadata, "responseModalities") ?? ["TEXT", "IMAGE"];
        var aspectRatio = request.AspectRatio ?? WgImageTryGetString(metadata, "aspectRatio");
        var imageSize = request.Size ?? WgImageTryGetString(metadata, "imageSize");

        var payloadObj = new Dictionary<string, object?>
        {
            ["contents"] = new[]
            {
                new
                {
                    role = "user",
                    parts
                }
            },
            ["generationConfig"] = new Dictionary<string, object?>
            {
                ["responseModalities"] = responseModalities,
                ["imageConfig"] = new
                {
                    aspectRatio,
                    imageSize
                }
            }
        };

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                payloadObj["tools"] = JsonSerializer.Deserialize<object>(toolsEl.GetRawText(), JsonSerializerOptions.Web);

            if (metadata.TryGetProperty("safetySettings", out var safetyEl) && safetyEl.ValueKind == JsonValueKind.Array)
                payloadObj["safetySettings"] = JsonSerializer.Deserialize<object>(safetyEl.GetRawText(), JsonSerializerOptions.Web);

            if (metadata.TryGetProperty("systemInstruction", out var sysEl) && sysEl.ValueKind == JsonValueKind.Object)
                payloadObj["systemInstruction"] = JsonSerializer.Deserialize<object>(sysEl.GetRawText(), JsonSerializerOptions.Web);
        }

        var route = $"v1beta/models/{request.Model}:generateContent";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(JsonSerializer.Serialize(payloadObj, WisdomGateImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Add("x-goog-api-key", key);

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WisdomGate image request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        List<string> images = [];
        WgCollectGeminiImages(root, images);
        if (images.Count == 0)
            throw new InvalidOperationException("WisdomGate image response did not contain image data.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = WgExtractUsage(root),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    endpoint = route,
                    body = root.Clone()
                }, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static void WgCollectGeminiImages(JsonElement element, List<string> images)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (WgTryGetInlineDataUrl(element, out var dataUrl))
                    images.Add(dataUrl);

                foreach (var property in element.EnumerateObject())
                    WgCollectGeminiImages(property.Value, images);

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                    WgCollectGeminiImages(item, images);

                break;
            }
        }
    }

    private static bool WgTryGetInlineDataUrl(JsonElement obj, out string dataUrl)
    {
        dataUrl = string.Empty;

        if (!WgTryGetPropertyIgnoreCase(obj, "data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
            return false;

        var b64 = dataEl.GetString();
        if (string.IsNullOrWhiteSpace(b64))
            return false;

        var mimeType = MediaTypeNames.Image.Png;
        if (WgTryGetPropertyIgnoreCase(obj, "mimeType", out var mimeEl) && mimeEl.ValueKind == JsonValueKind.String)
        {
            var mt = mimeEl.GetString();
            if (!string.IsNullOrWhiteSpace(mt) && mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mimeType = mt;
        }

        dataUrl = b64.ToDataUrl(mimeType);
        return true;
    }

    private static ImageUsageData? WgExtractUsage(JsonElement root)
    {
        if (root.TryGetProperty("usageMetadata", out var usageMetadata) && usageMetadata.ValueKind == JsonValueKind.Object)
        {
            var input = WgImageTryGetInt(usageMetadata, "promptTokenCount");
            var output = WgImageTryGetInt(usageMetadata, "candidatesTokenCount");
            var total = WgImageTryGetInt(usageMetadata, "totalTokenCount");

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

    private static bool WgTryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
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

    private static string? WgImageTryGetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int? WgImageTryGetInt(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
            : null;

    private static string[]? WgImageTryGetStringArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;

        return [.. el.EnumerateArray().Where(static a => a.ValueKind == JsonValueKind.String).Select(static a => a.GetString()).Where(static s => !string.IsNullOrWhiteSpace(s))!];
    }
}

