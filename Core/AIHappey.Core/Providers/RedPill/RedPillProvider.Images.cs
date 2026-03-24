using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RedPill;

public partial class RedPillProvider
{
    private static readonly JsonSerializerOptions RedPillImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> RedPillImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var requestedN = request.N ?? 1;
        if (requestedN < 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = $"Requested n={requestedN} is invalid. Using n=1."
            });
            requestedN = 1;
        }

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "RedPill image support currently uses text-to-image only. Ignored files."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "RedPill image support currently uses text-to-image only. Ignored mask."
            });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "RedPill /v1/images/generations expects size. aspectRatio is ignored."
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "RedPill seed passthrough is not implemented for text-to-image. Ignored seed."
            });
        }

        var payload = new
        {
            model = NormalizeProviderModelId(request.Model),
            prompt = request.Prompt,
            n = requestedN,
            size = request.Size,
            response_format = "b64_json"
        };

        var json = JsonSerializer.Serialize(payload, RedPillImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"{response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new Exception("RedPill image generation returned no data array.");

        List<string> images = [];

        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl("image/png"));
        }

        if (images.Count == 0)
            throw new Exception("RedPill image generation returned no b64_json images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private string NormalizeProviderModelId(string requestedModel)
    {
        var model = requestedModel.Trim();
        var providerPrefix = GetIdentifier() + "/";
        return model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? model[providerPrefix.Length..]
            : model;
    }
}
