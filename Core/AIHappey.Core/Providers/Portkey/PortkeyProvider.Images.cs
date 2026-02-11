using AIHappey.Core.AI;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Portkey;

public partial class PortkeyProvider
{
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
        var files = request.Files?.ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio"
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        HttpResponseMessage httpResponse;

        // Routing rules:
        // 1) mask present => image edit
        // 2) files present => image variation
        // 3) otherwise => image generation
        if (request.Mask is not null)
        {
            if (files.Count == 0)
                throw new ArgumentException("Portkey image edit requires one input image in files[0].", nameof(request));

            if (files.Count > 1)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = "Multiple input images are not supported for edits; used files[0]."
                });
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent(request.Prompt), "prompt");
            form.Add(new StringContent("b64_json"), "response_format");

            if (request.N.HasValue)
                form.Add(new StringContent(request.N.Value.ToString()), "n");

            if (!string.IsNullOrWhiteSpace(request.Size))
                form.Add(new StringContent(request.Size), "size");

            form.Add(CreateImageContent(files[0]), "image", "image");
            form.Add(CreateImageContent(request.Mask), "mask", "mask");

            httpResponse = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        }
        else if (files.Count > 0)
        {
            if (files.Count > 1)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = "Multiple input images are not supported for variations; used files[0]."
                });
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent("b64_json"), "response_format");

            if (request.N.HasValue)
                form.Add(new StringContent(request.N.Value.ToString()), "n");

            if (!string.IsNullOrWhiteSpace(request.Size))
                form.Add(new StringContent(request.Size), "size");

            form.Add(CreateImageContent(files[0]), "image", "image");

            httpResponse = await _client.PostAsync("v1/images/variations", form, cancellationToken);
        }
        else
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["prompt"] = request.Prompt,
                ["n"] = request.N,
                ["size"] = request.Size,
                ["response_format"] = "b64_json"
            };

            var json = JsonSerializer.Serialize(payload);

            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
            {
                Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            httpResponse = await _client.SendAsync(req, cancellationToken);
        }

        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new Exception($"Portkey API error: {httpResponse.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var images = await ParseImagesAsync(doc.RootElement, cancellationToken);

        if (images.Count == 0)
            throw new Exception("Portkey image endpoint returned no images.");

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

    private static ByteArrayContent CreateImageContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.MediaType)
                ? MediaTypeNames.Application.Octet
                : file.MediaType);

        return content;
    }

    private async Task<List<string>> ParseImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));

                continue;
            }

            // Defensive fallback in case gateway still returns URL payloads.
            if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
                    images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Png));
                }
            }
        }

        return images;
    }
}
