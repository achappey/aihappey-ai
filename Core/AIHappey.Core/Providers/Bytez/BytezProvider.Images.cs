using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bytez;

public partial class BytezProvider
{
    private static readonly JsonSerializerOptions BytezImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.ProviderOptions is not null && request.ProviderOptions.Count > 0)
            warnings.Add(new { type = "unsupported", feature = "providerOptions" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Prompt
        };

        var modelRoute = NormalizeModelRoute(request.Model);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, modelRoute)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, BytezImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bytez image request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var doc = JsonDocument.Parse(createRaw);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Bytez image request failed: {errorEl.GetRawText()}");

        var outputUrl = root.TryGetProperty("output", out var outputEl) ? outputEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(outputUrl))
            throw new InvalidOperationException("Bytez image response missing 'output' URL.");

        using var fileResp = await _client.GetAsync(outputUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Bytez image download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessImageMediaType(outputUrl)
            ?? MediaTypeNames.Image.Png;

        return new ImageResponse
        {
            Images = [Convert.ToBase64String(fileBytes).ToDataUrl(mediaType)],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string NormalizeModelRoute(string model)
    {
        var route = (model ?? string.Empty).Trim().TrimStart('/');

        if (route.StartsWith("bytez/", StringComparison.OrdinalIgnoreCase))
            route = route["bytez/".Length..];

        return route;
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".png")) return "image/png";
        if (u.Contains(".jpg") || u.Contains(".jpeg")) return "image/jpeg";
        if (u.Contains(".webp")) return "image/webp";
        if (u.Contains(".gif")) return "image/gif";
        if (u.Contains(".bmp")) return "image/bmp";
        if (u.Contains(".avif")) return "image/avif";

        return null;
    }
}
