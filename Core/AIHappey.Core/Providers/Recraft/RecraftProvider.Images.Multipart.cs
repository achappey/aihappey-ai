using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Recraft;

public partial class RecraftProvider
{
    private async Task<ImageResponse> SendMultipartImageRequestAsync(
        ImageRequest request,
        string endpoint,
        string primaryFileField,
        bool promptRequired,
        bool promptSupported,
        bool maskRequired,
        bool sizeSupported,
        string defaultMime,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var file = request.Files?.FirstOrDefault()
            ?? throw new ArgumentException($"Recraft model '{request.Model}' requires one input image in files[0].", nameof(request));

        if (request.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input files are not supported. Used files[0]."
            });
        }

        if (promptRequired && string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException($"Recraft model '{request.Model}' requires prompt.", nameof(request));

        if (!promptSupported && !string.IsNullOrWhiteSpace(request.Prompt))
            warnings.Add(new { type = "unsupported", feature = "prompt" });

        if (!sizeSupported && !string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (maskRequired && request.Mask is null)
            throw new ArgumentException($"Recraft model '{request.Model}' requires mask.", nameof(request));

        if (!maskRequired && request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        using var form = new MultipartFormDataContent();

        var (fileBytes, fileMediaType) = DecodeImageFile(file);
        using var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(fileMediaType);
        form.Add(fileContent, primaryFileField, "image.bin");

        if (request.Mask is not null)
        {
            var (maskBytes, maskMediaType) = DecodeImageFile(request.Mask);
            using var maskContent = new ByteArrayContent(maskBytes);
            maskContent.Headers.ContentType = new MediaTypeHeaderValue(maskMediaType);
            form.Add(maskContent, "mask", "mask.bin");
        }

        if (promptSupported && !string.IsNullOrWhiteSpace(request.Prompt))
            form.Add(new StringContent(request.Prompt), "prompt");

        if (request.N.HasValue)
            form.Add(new StringContent(request.N.Value.ToString()), "n");

        if (sizeSupported && !string.IsNullOrWhiteSpace(request.Size))
            form.Add(new StringContent(request.Size), "size");

        form.Add(new StringContent("url"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = form
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ParseImagesFromResponseAsync(raw, defaultMime, cancellationToken);
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

