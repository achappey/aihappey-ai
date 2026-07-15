using System.Net.Mime;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private async Task<ImageResponse> OpperAIImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var providerOptions = GetOpperAIProviderOptions(request.ProviderOptions);
        var payload = BuildOpperAIImagePayload(request, providerOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v3/images")
        {
            Content = CreateOpperAIJsonContent(payload)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpperAI image generation failed ({(int)response.StatusCode})."
                : $"OpperAI image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = System.Text.Json.JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await ExtractOpperAIImagesAsync(root, cancellationToken);

        return new ImageResponse
        {
            Images = images,
            Warnings = [],
            Usage = ExtractOpperAIImageUsage(root),
            ProviderMetadata = CreateOpperAIMediaMetadata(new
            {
                endpoint = "v3/images",
                payload,
                response = root
            }),
            Response = new()
            {
                Timestamp = ResolveOpperAITimestamp(root, now),
                Headers = response.GetHeaders(),
                ModelId = (TryGetOpperAIString(root, "model") ?? request.Model).ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildOpperAIImagePayload(
        ImageRequest request,
        Dictionary<string, object?> providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = request.Size,
            ["aspect_ratio"] = request.AspectRatio,
            ["response_format"] = "b64_json"
        };

        var files = request.Files?.Where(file => file is not null).ToList() ?? [];
        if (files.Count == 1)
        {
            payload["image"] = NormalizeOpperAIInputFile(files[0].Data, files[0].MediaType);
        }
        else if (files.Count > 1)
        {
            payload["reference_images"] = files
                .Select(file => NormalizeOpperAIInputFile(file.Data, file.MediaType))
                .ToArray();
        }

        if (request.Mask is not null)
            payload["mask"] = NormalizeOpperAIInputFile(request.Mask.Data, request.Mask.MediaType);

        AddOpperAIParameters(payload, providerOptions);
        return payload;
    }

    private async Task<List<string>> ExtractOpperAIImagesAsync(
        System.Text.Json.JsonElement root,
        CancellationToken cancellationToken)
    {
        List<string> images = [];

        if (!TryGetOpperAIProperty(root, "data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            var mediaType = TryGetOpperAIString(item, "mime_type", "mimeType") ?? MediaTypeNames.Image.Png;
            var b64 = TryGetOpperAIString(item, "b64_json", "base64", "data");
            if (!string.IsNullOrWhiteSpace(b64))
            {
                images.Add(NormalizeOpperAIDataUrl(b64, mediaType));
                continue;
            }

            var url = TryGetOpperAIString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var downloaded = await DownloadOpperAIMediaAsync(url, mediaType, cancellationToken);
            images.Add(Convert.ToBase64String(downloaded.Bytes).ToDataUrl(downloaded.MediaType));
        }

        return images;
    }

    private static ImageUsageData? ExtractOpperAIImageUsage(System.Text.Json.JsonElement root)
    {
        if (!TryGetOpperAIProperty(root, "usage", out var usage) || usage.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        return new ImageUsageData
        {
            InputTokens = TryGetOpperAIDouble(usage, "input_tokens", "inputTokens") is { } input ? (int?)Convert.ToInt32(input) : null,
            OutputTokens = TryGetOpperAIDouble(usage, "output_tokens", "outputTokens", "images") is { } output ? (int?)Convert.ToInt32(output) : null,
            TotalTokens = TryGetOpperAIDouble(usage, "total_tokens", "totalTokens") is { } total ? (int?)Convert.ToInt32(total) : null
        };
    }
}
