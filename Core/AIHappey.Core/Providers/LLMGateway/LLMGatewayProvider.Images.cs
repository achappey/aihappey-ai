using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    private static readonly JsonSerializerOptions ImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestLLMGateway(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files"
            });
        }

        var imageConfig = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            imageConfig["aspect_ratio"] = imageRequest.AspectRatio;

        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
            imageConfig["image_size"] = imageRequest.Size;

        if (imageRequest.N is not null)
            imageConfig["n"] = imageRequest.N;

        if (imageRequest.Seed is not null)
            imageConfig["seed"] = imageRequest.Seed;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = imageRequest.Prompt
                }
            }
        };

        if (imageConfig.Count > 0)
            payload["image_config"] = imageConfig;

        var jsonBody = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var jsonResponse = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {jsonResponse}");

        using var doc = JsonDocument.Parse(jsonResponse);

        var images = ExtractImages(doc.RootElement);

        if (images.Count == 0)
            throw new Exception("No generated images returned from LLM Gateway API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractUsage(doc.RootElement),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static List<string> ExtractImages(JsonElement root)
    {
        List<string> images = [];

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            if (!message.TryGetProperty("images", out var imageParts) || imageParts.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var imagePart in imageParts.EnumerateArray())
            {
                if (imagePart.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && !string.Equals(typeEl.GetString(), "image_url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!imagePart.TryGetProperty("image_url", out var imageUrl) || imageUrl.ValueKind != JsonValueKind.Object)
                    continue;

                if (!imageUrl.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                    continue;

                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                images.Add(url);
            }
        }

        return images;
    }

    private static ImageUsageData? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData();

        if (usage.TryGetProperty("prompt_tokens", out var inputTokensEl) && inputTokensEl.TryGetInt32(out var inputTokens))
            usageData.InputTokens = inputTokens;

        if (usage.TryGetProperty("completion_tokens", out var outputTokensEl) && outputTokensEl.TryGetInt32(out var outputTokens))
            usageData.OutputTokens = outputTokens;

        if (usage.TryGetProperty("total_tokens", out var totalTokensEl) && totalTokensEl.TryGetInt32(out var totalTokens))
            usageData.TotalTokens = totalTokens;

        if (!usageData.InputTokens.HasValue && !usageData.OutputTokens.HasValue && !usageData.TotalTokens.HasValue)
            return null;

        return usageData;
    }
}
