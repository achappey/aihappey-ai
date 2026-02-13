using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Recraft;

public partial class RecraftProvider
{
    private async Task<ImageResponse> GenerateImagesAsync(
        ImageRequest request,
        string modelName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var payload = new
        {
            prompt = request.Prompt,
            model = modelName,
            n = request.N,
            size = request.Size,
            response_format = "url"
        };

        var json = JsonSerializer.Serialize(payload, RecraftJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ParseImagesFromResponseAsync(raw, "image/png", cancellationToken);
        if (images.Count == 0)
            throw new Exception("Recraft response did not contain any images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }
}

