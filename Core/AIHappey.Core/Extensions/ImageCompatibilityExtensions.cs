using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Extensions;

public static class ImageCompatibilityExtensions
{
    private const string DefaultGenerationModel = "dall-e-2";
    private const string DefaultEditModel = "gpt-image-1.5";
    private const string DefaultVariationModel = "dall-e-2";
    private const string VariationFallbackPrompt = "Create a variation of the provided image.";

    public static string ResolveOpenAIImageGenerationModel(this OpenAIImageGenerationRequest request)
        => string.IsNullOrWhiteSpace(request.Model) ? DefaultGenerationModel : request.Model.Trim();

    public static string ResolveOpenAIImageEditModel(this OpenAIImageEditRequest request)
        => string.IsNullOrWhiteSpace(request.Model) ? DefaultEditModel : request.Model.Trim();

    public static string ResolveOpenAIImageVariationModel(this OpenAIImageVariationRequest request)
        => string.IsNullOrWhiteSpace(request.Model) ? DefaultVariationModel : request.Model.Trim();

    public static void ValidateOpenAIImageGenerationRequest(this OpenAIImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("'prompt' is a required field");
        ValidateImageCount(request.N);
        ValidatePartialImages(request.PartialImages);
        ValidateOutputCompression(request.OutputCompression);
    }

    public static void ValidateOpenAIImageEditRequest(this OpenAIImageEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("'prompt' is a required field");
        if ((request.ImageFiles?.Length ?? 0) == 0 && (request.Images?.Length ?? 0) == 0)
            throw new ArgumentException("At least one image is required");
        ValidateImageCount(request.N);
        ValidatePartialImages(request.PartialImages);
        ValidateOutputCompression(request.OutputCompression);
    }

    public static void ValidateOpenAIImageVariationRequest(this OpenAIImageVariationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ImageFile == null && request.Image == null)
            throw new ArgumentException("'image' is a required field");
        ValidateImageCount(request.N);
    }

    public static ImageRequest ToImageRequest(this OpenAIImageGenerationRequest request, string model, string providerIdentifier)
        => new()
        {
            Model = model,
            Prompt = request.Prompt,
            Size = request.Size,
            N = request.N,
            ProviderOptions = BuildProviderOptions(providerIdentifier, new()
            {
            /*    ["background"] = request.Background,
                ["moderation"] = request.Moderation,
                ["output_compression"] = request.OutputCompression,
                ["output_format"] = request.OutputFormat,
                ["partial_images"] = request.PartialImages,
                ["quality"] = request.Quality,
                ["response_format"] = request.ResponseFormat,
                ["stream"] = request.Stream,
                ["style"] = request.Style,
                ["user"] = request.User*/
            })
        };

    public static async Task<ImageRequest> ToImageRequest(this OpenAIImageEditRequest request, string model, string providerIdentifier, CancellationToken cancellationToken = default)
        => new()
        {
            Model = model,
            Prompt = request.Prompt,
            Size = request.Size,
            N = request.N,
            Files = await ResolveImageFiles(request.ImageFiles, request.Images, cancellationToken),
            Mask = await ResolveImageFile(request.MaskFile, request.Mask, cancellationToken),
            ProviderOptions = BuildProviderOptions(providerIdentifier, new()
            {
            /*    ["background"] = request.Background,
                ["input_fidelity"] = request.InputFidelity,
                ["moderation"] = request.Moderation,
                ["output_compression"] = request.OutputCompression,
                ["output_format"] = request.OutputFormat,
                ["partial_images"] = request.PartialImages,
                ["quality"] = request.Quality,
                ["stream"] = request.Stream,
                ["user"] = request.User*/
            })
        };

    public static async Task<ImageRequest> ToImageRequest(this OpenAIImageVariationRequest request, string model, string providerIdentifier, CancellationToken cancellationToken = default)
        => new()
        {
            Model = model,
            Prompt = VariationFallbackPrompt,
            Size = request.Size,
            N = request.N,
            Files = [await ResolveRequiredImageFile(request.ImageFile, request.Image, cancellationToken)],
            ProviderOptions = BuildProviderOptions(providerIdentifier, new()
            {
               // ["response_format"] = request.ResponseFormat,
               // ["user"] = request.User
            })
        };

    public static OpenAIImageEditRequest ToOpenAIImageEditRequest(this IFormCollection form)
        => new()
        {
            ImageFiles = ReadImageFiles(form),
            Prompt = ReadFormString(form, "prompt")!,
            Background = ReadFormString(form, "background"),
            InputFidelity = ReadFormString(form, "input_fidelity"),
            MaskFile = form.Files.GetFile("mask"),
            Model = ReadFormString(form, "model"),
            Moderation = ReadFormString(form, "moderation"),
            N = ReadFormInt(form, "n"),
            OutputCompression = ReadFormInt(form, "output_compression"),
            OutputFormat = ReadFormString(form, "output_format"),
            PartialImages = ReadFormInt(form, "partial_images"),
            Quality = ReadFormString(form, "quality"),
            Size = ReadFormString(form, "size"),
            Stream = ReadFormBool(form, "stream"),
            User = ReadFormString(form, "user")
        };

    public static OpenAIImageVariationRequest ToOpenAIImageVariationRequest(this IFormCollection form)
        => new()
        {
            ImageFile = form.Files.GetFile("image"),
            Model = ReadFormString(form, "model"),
            N = ReadFormInt(form, "n"),
            ResponseFormat = ReadFormString(form, "response_format"),
            Size = ReadFormString(form, "size"),
            User = ReadFormString(form, "user")
        };

    public static OpenAIImagesResponse ToOpenAIImagesResponse(this ImageResponse response, OpenAIImageGenerationRequest request)
        => response.ToOpenAIImagesResponse(request.Background, request.OutputFormat, request.Quality, request.Size, request.ResponseFormat);

    public static OpenAIImagesResponse ToOpenAIImagesResponse(this ImageResponse response, OpenAIImageEditRequest request)
        => response.ToOpenAIImagesResponse(request.Background, request.OutputFormat, request.Quality, request.Size, null);

    public static OpenAIImagesResponse ToOpenAIImagesResponse(this ImageResponse response, OpenAIImageVariationRequest request)
        => response.ToOpenAIImagesResponse(null, null, null, request.Size, request.ResponseFormat);

    public static IEnumerable<IOpenAIImageStreamEvent> ToOpenAIImageGenerationCompletedEvents(this ImageResponse response, OpenAIImageGenerationRequest request)
    {
        var createdAt = response.GetCreatedUnixTime();
        var usage = response.Usage.ToOpenAIImageUsage();
        var index = 0;
        foreach (var image in response.Images ?? [])
        {
            var (_, b64) = ExtractImagePayload(image);
            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = b64,
                CreatedAt = createdAt,
                Background = request.Background,
                OutputFormat = request.OutputFormat,
                Quality = request.Quality,
                Size = request.Size,
                Usage = index++ == 0 ? usage : null
            };
        }
    }

    public static IEnumerable<IOpenAIImageStreamEvent> ToOpenAIImageEditCompletedEvents(this ImageResponse response, OpenAIImageEditRequest request)
    {
        var createdAt = response.GetCreatedUnixTime();
        var usage = response.Usage.ToOpenAIImageUsage();
        var index = 0;
        foreach (var image in response.Images ?? [])
        {
            var (_, b64) = ExtractImagePayload(image);
            yield return new OpenAIImageEditCompleted
            {
                B64Json = b64,
                CreatedAt = createdAt,
                Background = request.Background,
                OutputFormat = request.OutputFormat,
                Quality = request.Quality,
                Size = request.Size,
                Usage = index++ == 0 ? usage : null
            };
        }
    }

    private static OpenAIImagesResponse ToOpenAIImagesResponse(this ImageResponse response, string? background, string? outputFormat, string? quality, string? size, string? responseFormat)
    {
        var preferUrl = string.Equals(responseFormat, "url", StringComparison.OrdinalIgnoreCase);
        return new OpenAIImagesResponse
        {
            Created = response.GetCreatedUnixTime(),
            Background = string.Equals(background, "auto", StringComparison.OrdinalIgnoreCase) ? null : background,
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Usage = response.Usage.ToOpenAIImageUsage(),
            Data = (response.Images ?? []).Where(image => !string.IsNullOrWhiteSpace(image)).Select(image => ToOpenAIImageData(image, preferUrl)).ToList()
        };
    }

    private static OpenAIImageData ToOpenAIImageData(string image, bool preferUrl)
    {
        if (preferUrl && Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return new OpenAIImageData { Url = image };
        var (_, b64) = ExtractImagePayload(image);
        return new OpenAIImageData { B64Json = b64 };
    }

    private static OpenAIImageUsage? ToOpenAIImageUsage(this ImageUsageData? usage)
        => usage == null ? null : new OpenAIImageUsage { InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens };

    private static long GetCreatedUnixTime(this ImageResponse response)
    {
        var timestamp = response.Response?.Timestamp;
        if (timestamp is { } created && created != default)
            return new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static Dictionary<string, JsonElement>? BuildProviderOptions(string providerIdentifier, Dictionary<string, object?> options)
    {
        var filtered = options.Where(kvp => kvp.Value is not null && (kvp.Value is not string value || !string.IsNullOrWhiteSpace(value))).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return filtered.Count == 0 ? null : new Dictionary<string, JsonElement> { [providerIdentifier] = JsonSerializer.SerializeToElement(filtered, JsonSerializerOptions.Web) };
    }

    private static async Task<IEnumerable<ImageFile>?> ResolveImageFiles(IFormFile[]? files, OpenAIImageReference[]? references, CancellationToken cancellationToken)
    {
        var resolved = new List<ImageFile>();
        foreach (var file in files ?? [])
            resolved.Add(await ToImageFile(file, cancellationToken));
        foreach (var reference in references ?? [])
            resolved.Add(ToImageFile(reference));
        return resolved.Count == 0 ? null : resolved;
    }

    private static async Task<ImageFile?> ResolveImageFile(IFormFile? file, OpenAIImageReference? reference, CancellationToken cancellationToken)
    {
        if (file != null)
            return await ToImageFile(file, cancellationToken);
        return reference == null ? null : ToImageFile(reference);
    }

    private static async Task<ImageFile> ResolveRequiredImageFile(IFormFile? file, OpenAIImageReference? reference, CancellationToken cancellationToken)
        => await ResolveImageFile(file, reference, cancellationToken) ?? throw new ArgumentException("'image' is a required field");

    private static async Task<ImageFile> ToImageFile(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return new ImageFile { Type = "file", MediaType = ResolveImageMediaType(file), Data = Convert.ToBase64String(memory.ToArray()) };
    }

    private static ImageFile ToImageFile(OpenAIImageReference reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.FileId))
            throw new NotSupportedException("OpenAI image file_id references are not supported by this compatibility fallback yet.");
        if (string.IsNullOrWhiteSpace(reference.ImageUrl))
            throw new ArgumentException("Image references require either 'image_url' or 'file_id'.");
        if (!reference.ImageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only base64 data URL image_url references are supported by this compatibility fallback yet.");
        var (mediaType, data) = ExtractImagePayload(reference.ImageUrl);
        return new ImageFile { Type = "file", MediaType = mediaType ?? "image/png", Data = data };
    }

    private static (string? MediaType, string Base64) ExtractImagePayload(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Image data cannot be empty.");
        if (!input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return (null, input.Trim());
        var commaIndex = input.IndexOf(',');
        if (commaIndex < 0 || commaIndex == input.Length - 1)
            throw new FormatException("Invalid image data URL.");
        var header = input[5..commaIndex];
        var payload = input[(commaIndex + 1)..].Trim();
        var semiIndex = header.IndexOf(';');
        var mediaType = semiIndex >= 0 ? header[..semiIndex] : header;
        return (string.IsNullOrWhiteSpace(mediaType) ? null : mediaType, payload);
    }

    private static IFormFile[]? ReadImageFiles(IFormCollection form)
    {
        var files = form.Files.Where(file => file.Name is "image" or "image[]" or "images" or "images[]").ToArray();
        return files.Length == 0 ? null : files;
    }

    private static string? ReadFormString(IFormCollection form, string name)
        => form.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.FirstOrDefault()) ? value.FirstOrDefault() : null;

    private static int? ReadFormInt(IFormCollection form, string name)
        => int.TryParse(ReadFormString(form, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? ReadFormBool(IFormCollection form, string name)
        => ReadFormString(form, name)?.Trim().ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => null };

    private static string ResolveImageMediaType(IFormFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.ContentType) && file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return file.ContentType;
        return Path.GetExtension(file.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
    }

    private static void ValidateImageCount(int? n)
    {
        if (n is < 1 or > 10)
            throw new ArgumentException("'n' must be between 1 and 10");
    }

    private static void ValidatePartialImages(int? partialImages)
    {
        if (partialImages is < 0 or > 3)
            throw new ArgumentException("'partial_images' must be between 0 and 3");
    }

    private static void ValidateOutputCompression(int? outputCompression)
    {
        if (outputCompression is < 0 or > 100)
            throw new ArgumentException("'output_compression' must be between 0 and 100");
    }
}
