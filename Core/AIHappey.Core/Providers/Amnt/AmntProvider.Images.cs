using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Amnt;

public partial class AmntProvider
{
    private const int MaxPngBytes = 20 * 1024 * 1024;

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vectorize = request.Model == "svg/vectorize";
        if (!vectorize && request.Model != "amnt-svg-1.1")
            throw new NotSupportedException("Only AMNT SVG 1.1 and the svg/vectorize route are documented.");
        if (request.Mask is not null || request.Files?.Skip(1).Any() == true)
            throw new NotSupportedException("Amnt SVG supports one input image and no mask.");
        if (request.N is not null and not 1)
            throw new NotSupportedException("Amnt SVG generates one image per paid call.");
        if (request.Files?.Any() == true && !vectorize)
            throw new NotSupportedException("Use amnt/svg/vectorize to submit an input image.");
        if (request.Seed is not null || request.Size is not null || request.AspectRatio is not null)
            throw new NotSupportedException("Seed, size and aspect ratio require documented Amnt SVG options.");

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object) throw new ArgumentException("Amnt provider options must be an object.");
            foreach (var field in options.EnumerateObject())
            {
                if (field.Name is "model" or "prompt" or "image")
                    throw new ArgumentException($"Amnt option '{field.Name}' is controlled by the image contract.");
                payload[field.Name] = field.Value.Clone();
            }
        }
        if (!vectorize)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.");
            payload["model"] = request.Model;
            payload["prompt"] = request.Prompt;
        }
        else
        {
            var file = request.Files?.SingleOrDefault();
            if (file is null || string.IsNullOrWhiteSpace(file.Data))
                throw new ArgumentException("SVG vectorize requires one image input.");
            payload["image"] = NormalizeInput(file);
            if (!string.IsNullOrWhiteSpace(request.Prompt)) payload["prompt"] = request.Prompt;
        }
        var (body, headers) = await SendAsync(HttpMethod.Post,
            vectorize ? "api/v1/svg/vectorize" : "api/v1/svg/generate", payload, ct);
        var png = Property(body, "files") is { } files ? String(files, "png") : null;
        if (string.IsNullOrWhiteSpace(png))
            throw new InvalidOperationException("Amnt did not provide a PNG file for the image contract.");
        var bytes = await DownloadPngAsync(png, ct);
        return new ImageResponse
        {
            Images = [$"data:image/png;base64,{Convert.ToBase64String(bytes)}"],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = headers,
                ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    private static string NormalizeInput(ImageFile file)
    {
        if (file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return file.Data;
        if (file.Data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return file.Data;
        if (file.Type == "url" || file.Type == "file_id") throw new ArgumentException("Amnt vectorize requires HTTPS or inline image content.");
        if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            || file.MediaType.Contains(';') || file.MediaType.Contains(','))
            throw new ArgumentException("A valid image media type is required.");
        try { Convert.FromBase64String(file.Data); }
        catch (FormatException ex) { throw new ArgumentException("Image data must be base64.", ex); }
        return $"data:{file.MediaType};base64,{file.Data}";
    }

    private async Task<byte[]> DownloadPngAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "www.amnt.io", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Amnt supplied an untrusted PNG URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != uri) throw new InvalidOperationException("PNG redirects are not allowed.");
        if (response.Content.Headers.ContentType?.MediaType != MediaTypeNames.Image.Png
            || response.Content.Headers.ContentLength is > MaxPngBytes)
            throw new InvalidOperationException("Amnt PNG has an invalid content type or size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > MaxPngBytes) throw new InvalidOperationException("Amnt PNG exceeded the size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken ct = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        return (await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), ct)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var result = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), ct);
        foreach (var item in result.ToOpenAIImageGenerationCompletedEvents(options)) yield return item;
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken ct = default)
    {
        options.ValidateOpenAIImageEditRequest();
        return (await ImageRequest(await options.ToImageRequest(options.Model, GetIdentifier(), ct), ct)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var result = await ImageRequest(await options.ToImageRequest(options.Model, GetIdentifier(), ct), ct);
        foreach (var item in result.ToOpenAIImageEditCompletedEvents(options)) yield return item;
    }
}
