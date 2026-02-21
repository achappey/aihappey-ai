using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Parasail;

public partial class ParasailProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Parasail image endpoint currently supports prompt + optional input images only. Ignored mask."
            });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspect_ratio",
                details = "Parasail expects size as WxH. Ignored aspect_ratio."
            });
        }

        if (request.Seed is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "Parasail endpoint docs do not specify seed support. Ignored seed."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["response_format"] = "b64_json"
        };

        if (request.Files?.Any() == true)
        {
            payload["image"] = request.Files
                .Where(a => !string.IsNullOrWhiteSpace(a.Data))
                .Select(a => a.Data.RemoveDataUrlPrefix())
                .ToArray();
        }

        if (request.ProviderOptions is not null &&
            request.ProviderOptions.TryGetValue(GetIdentifier(), out var parasailOptionsEl) &&
            parasailOptionsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parasailOptionsEl.EnumerateObject())
            {
                if (property.NameEquals("model") ||
                    property.NameEquals("prompt") ||
                    property.NameEquals("n") ||
                    property.NameEquals("size") ||
                    property.NameEquals("image") ||
                    property.NameEquals("response_format"))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        var json = JsonSerializer.Serialize(payload, ImageJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Parasail API error: {(int)resp.StatusCode} {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = new List<string>();
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                    continue;

                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl("image/png"));
            }
        }

        if (images.Count == 0)
            throw new Exception("Parasail image generation returned no b64_json images.");

        ImageUsageData? usage = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            usage = new ImageUsageData
            {
                InputTokens = usageEl.TryGetProperty("input_tokens", out var inputEl) && inputEl.TryGetInt32(out var input)
                    ? input
                    : null,
                OutputTokens = usageEl.TryGetProperty("output_tokens", out var outputEl) && outputEl.TryGetInt32(out var output)
                    ? output
                    : null,
                TotalTokens = usageEl.TryGetProperty("total_tokens", out var totalEl) && totalEl.TryGetInt32(out var total)
                    ? total
                    : null
            };
        }

        var timestamp = now;
        if (root.TryGetProperty("created", out var createdEl) &&
            createdEl.ValueKind == JsonValueKind.Number &&
            createdEl.TryGetInt64(out var createdUnix))
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(createdUnix).UtcDateTime;
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = usage,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }
}
