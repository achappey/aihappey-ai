using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Sudo;

public partial class SudoProvider 
{
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

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Sudo images/generations currently supports text-to-image only. Ignored files."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Sudo images/generations currently supports text-to-image only. Ignored mask."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size
        };

        if (request.ProviderOptions is not null &&
            request.ProviderOptions.TryGetValue(GetIdentifier(), out var sudoOptionsEl) &&
            sudoOptionsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in sudoOptionsEl.EnumerateObject())
            {
                if (property.NameEquals("model") ||
                    property.NameEquals("prompt") ||
                    property.NameEquals("n") ||
                    property.NameEquals("size") ||
                    property.NameEquals("response_format"))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        // Always force base64 response to keep provider output contract consistent.
        payload["response_format"] = "b64_json";

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Sudo API error: {(int)resp.StatusCode} {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var images = new List<string>();

        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (!item.TryGetProperty("b64_json", out var b64El) ||
                    b64El.ValueKind != JsonValueKind.String)
                    continue;

                var b64 = b64El.GetString();

                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl("image/png"));
            }
        }

        if (images.Count == 0)
            throw new Exception("Sudo image generation returned no b64_json images.");

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
            createdEl.ValueKind is JsonValueKind.Number &&
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
