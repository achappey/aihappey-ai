using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Radient;

public partial class RadientProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await GenerateImagesAsync(CreateImagePayload(request), cancellationToken);
        return new ImageResponse
        {
            Images = await DownloadImagesAsync(result.Images, cancellationToken),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = result.Headers
            }
        };
    }

    private Dictionary<string, object?> CreateImagePayload(ImageRequest request)
    {
        var payload = CopyMetadata(request.ProviderOptions);
        payload.Remove("model");
        payload.Remove("provider");
        payload["prompt"] = request.Prompt;
        payload["num_images"] = request.N ?? 1;
        payload["sync_mode"] = false;
        Set(payload, "image_size", ResolveImageSize(request.Size, request.AspectRatio));
        Set(payload, "seed", request.Seed);
        var source = request.Files?.FirstOrDefault();
        if (source is not null) payload["source_url"] = $"data:{source.MediaType};base64,{source.Data}";
        if (request.Mask is not null) throw new NotSupportedException("Radient image generation does not document mask editing.");
        return payload;
    }

    private async Task<RadientImageResult> GenerateImagesAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/images/generate",
            new StringContent(JsonSerializer.Serialize(payload, RadientJson), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Radient image request failed ({(int)response.StatusCode}): {raw}");
        var root = JsonSerializer.Deserialize<JsonElement>(raw);
        var headers = response.GetHeaders();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return new RadientImageResult(ReadImageUrls(root), root, headers);
            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Radient image generation failed: " + ReadString(root, "error"));
            var requestId = ReadString(root, "request_id");
            if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidOperationException("Radient image response had neither images nor a request_id.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var poll = await _client.GetAsync($"v1/images/status?request_id={Uri.EscapeDataString(requestId)}", cancellationToken);
            raw = await poll.Content.ReadAsStringAsync(cancellationToken);
            if (!poll.IsSuccessStatusCode) throw new InvalidOperationException($"Radient image status failed ({(int)poll.StatusCode}): {raw}");
            root = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        throw new TimeoutException("Radient image generation did not complete within 80 seconds.");
    }

    private async Task<List<string>> DownloadImagesAsync(List<string> urls, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        foreach (var url in urls)
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { images.Add(url); continue; }
            using var response = await _client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            images.Add($"data:{response.Content.Headers.ContentType?.MediaType ?? "image/png"};base64,{Convert.ToBase64String(bytes)}");
        }
        return images;
    }

    private static List<string> ReadImageUrls(JsonElement root) => root.TryGetProperty("images", out var images)
        && images.ValueKind == JsonValueKind.Array
        ? images.EnumerateArray().Select(x => ReadString(x, "url")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() : [];
    private static string? ResolveImageSize(string? size, string? ratio) => ratio?.Replace(':', '_') switch
    { "1_1" => "square", "4_3" => "landscape_4_3", "3_4" => "portrait_4_3", "16_9" => "landscape_16_9", "9_16" => "portrait_16_9", _ => size switch { "1024x1024" => "square_hd", _ => size } };
    private sealed record RadientImageResult(List<string> Images, JsonElement Root, Dictionary<string, string> Headers);
}
