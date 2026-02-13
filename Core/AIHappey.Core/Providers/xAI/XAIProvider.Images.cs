using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var now = DateTime.UtcNow;

        var payload = new
        {
            model = imageRequest.Model,
            prompt = imageRequest.Prompt,
            n = imageRequest.N,
            response_format = "b64_json"
        };

        List<object> warnings = [];
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // 3) Send request
        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(string.IsNullOrWhiteSpace(raw) ? resp.ReasonPhrase : raw);

        // 4) Parse response
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data returned");

        List<ResourceLinkBlock> resourceLinks = [];
        List<string> images = [];
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var bytes = Convert.FromBase64String(b64El.GetString()!);

            images.Add(b64El.GetString()!.ToDataUrl("image/png"));
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Size is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aize"
            });
        }

        return new()
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }

}
