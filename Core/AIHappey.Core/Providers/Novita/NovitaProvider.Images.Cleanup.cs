using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Novita;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    private static readonly JsonSerializerOptions CleanupTextJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestCleanup(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = request.GetImageProviderMetadata<NovitaImageProviderMetadata>(GetIdentifier());
        var removeText = providerMetadata?.Editor;

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "prompt",
                details = "Remove Text ignores prompt. Input image is required."
            });
        }

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Remove Text returns a single image per request in this integration."
            });
        }

        if (request.Mask is null)
            throw new ArgumentException("An mask image is required.", nameof(request));

        if (request.Files is null || !request.Files.Any())
            throw new ArgumentException("An input image is required in files[0].", nameof(request));

        if (request.Files.Skip(1).Any())
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images are not supported; used files[0]."
            });
        }

        var inputFile = request.Files.First();
        var imageBase64 = inputFile.Data.RemoveDataUrlPrefix();
        var maskImageBase64 = request.Mask.Data.RemoveDataUrlPrefix();

        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("files[0].data must contain base64 image data.", nameof(request));

        if (string.IsNullOrWhiteSpace(maskImageBase64))
            throw new ArgumentException("Mask.data must contain base64 image data.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["image_file"] = imageBase64,
            ["mask_file"] = maskImageBase64
        };

        if (!string.IsNullOrWhiteSpace(removeText?.Extra?.ResponseImageType))
        {
            var extra = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(removeText?.Extra?.ResponseImageType))
                extra["response_image_type"] = removeText.Extra?.ResponseImageType;

            payload["extra"] = extra;
        }

        var json = JsonSerializer.Serialize(payload, RemoveTextJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
            new Uri("https://api.novita.ai/v3/" + request.Model))
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("image_file", out var imageFileEl) || imageFileEl.ValueKind != JsonValueKind.String)
            throw new Exception("Novita Cleanup response did not include image_file.");

        var returnedBase64 = imageFileEl.GetString();
        if (string.IsNullOrWhiteSpace(returnedBase64))
            throw new Exception("Novita Cleanup response image_file was empty.");

        var imageType = "png";
        if (root.TryGetProperty("image_type", out var imageTypeEl) && imageTypeEl.ValueKind == JsonValueKind.String)
            imageType = imageTypeEl.GetString() ?? imageType;

        var mimeType = imageType.ToLowerInvariant() switch
        {
            "webp" => "image/webp",
            "jpeg" => MediaTypeNames.Image.Jpeg,
            "jpg" => MediaTypeNames.Image.Jpeg,
            _ => MediaTypeNames.Image.Png
        };

        var images = new List<string> { returnedBase64.ToDataUrl(mimeType) };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    public static bool IsCleanupModel(string? model)
        => model?.Contains("cleanup", StringComparison.OrdinalIgnoreCase) == true;
}
