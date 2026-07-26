using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Models;
using AIHappey.Interactions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);
        ValidateGoogleImageRequest(imageRequest.Model, imageRequest.Prompt);

        var input = new List<InteractionContent>
        {
            new InteractionTextContent { Text = imageRequest.Prompt }
        };

        foreach (var file in imageRequest.Files ?? [])
            input.Add(CreateGoogleImageContent(file));

        var interaction = await GetInteraction(CreateGoogleImageInteractionRequest(
            imageRequest.Model,
            input,
            imageRequest.AspectRatio,
            imageRequest.Size,
            MediaTypeNames.Image.Png), cancellationToken);

        var images = ExtractGoogleInteractionImages(interaction);
        if (images.Count == 0)
            throw new InvalidOperationException("Google image response did not contain a generated image.");

        return new ImageResponse
        {
            Images = images.Select(image => image.Data.ToDataUrl(image.MimeType)),
            Warnings = [],
            Response = new()
            {
                Timestamp = ParseGoogleInteractionTimestamp(interaction.Created),
                ModelId = NormalizeGoogleImageModel(interaction.Model ?? imageRequest.Model)
            },
            Usage = ToVercelImageUsage(interaction.Usage)
        };
    }

    private static InteractionRequest CreateGoogleImageInteractionRequest(
        string model,
        List<InteractionContent> input,
        string? aspectRatio,
        string? size,
        string? mimeType)
    {
        var (normalizedAspectRatio, imageSize) = NormalizeGoogleImageDimensions(aspectRatio, size);
        var responseFormat = new Dictionary<string, object?>
        {
            ["type"] = "image",
            ["mime_type"] = NormalizeGoogleImageMimeType(mimeType)
        };

        if (!string.IsNullOrWhiteSpace(normalizedAspectRatio))
            responseFormat["aspect_ratio"] = normalizedAspectRatio;
        if (!string.IsNullOrWhiteSpace(imageSize))
            responseFormat["image_size"] = imageSize;

        return new InteractionRequest
        {
            Model = NormalizeGoogleImageModel(model),
            Input = new InteractionsInput(input),
            ResponseFormat = responseFormat,
            Store = false
        };
    }

    private static InteractionImageContent CreateGoogleImageContent(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));

        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return new InteractionImageContent
            {
                Uri = file.Data,
                MimeType = string.IsNullOrWhiteSpace(file.MediaType) ? null : file.MediaType
            };
        }

        return new InteractionImageContent
        {
            Data = file.Data.RemoveDataUrlPrefix(),
            MimeType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType
        };
    }

    private static void ValidateGoogleImageRequest(string? model, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        if (model.Contains("imagen", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Google Imagen models are not supported. Use a Gemini native image model.");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));
    }

    private static string NormalizeGoogleImageModel(string model)
    {
        var normalized = model.Trim();
        var providerPrefix = GoogleExtensions.Identifier() + "/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..];
        if (normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["models/".Length..];
        return normalized;
    }

    private static (string? AspectRatio, string? ImageSize) NormalizeGoogleImageDimensions(string? aspectRatio, string? size)
    {
        var normalizedSize = size?.Trim().ToUpperInvariant();
        if (normalizedSize is "512PX" or "0.5K")
            return (aspectRatio, "0.5K");
        if (normalizedSize is "1K" or "2K" or "4K")
            return (aspectRatio, normalizedSize);

        if (!string.IsNullOrWhiteSpace(size))
        {
            var dimensions = size.ToLowerInvariant().Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (dimensions.Length == 2
                && int.TryParse(dimensions[0], out var width)
                && int.TryParse(dimensions[1], out var height)
                && width > 0 && height > 0)
            {
                var divisor = GreatestCommonDivisor(width, height);
                var ratio = $"{width / divisor}:{height / divisor}";
                var longest = Math.Max(width, height);
                var resolution = longest <= 768 ? "0.5K" : longest <= 1536 ? "1K" : longest <= 3072 ? "2K" : "4K";
                return (aspectRatio ?? ratio, resolution);
            }
        }

        return (aspectRatio, null);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static string NormalizeGoogleImageMimeType(string? mimeType)
        => mimeType?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" or "image/jpg" or "image/jpeg" => MediaTypeNames.Image.Jpeg,
            _ => MediaTypeNames.Image.Png
        };

    private static List<GoogleInteractionImage> ExtractGoogleInteractionImages(Interaction interaction)
        => interaction.Steps?
            .OfType<InteractionModelOutputStep>()
            .SelectMany(step => step.Content ?? [])
            .OfType<InteractionImageContent>()
            .Where(image => !string.IsNullOrWhiteSpace(image.Data))
            .Select(image => new GoogleInteractionImage(
                image.Data!.RemoveDataUrlPrefix(),
                string.IsNullOrWhiteSpace(image.MimeType) ? MediaTypeNames.Image.Png : image.MimeType!))
            .ToList() ?? [];

    private static DateTime ParseGoogleInteractionTimestamp(string? timestamp)
        => DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed.UtcDateTime : DateTime.UtcNow;

    private static ImageUsageData? ToVercelImageUsage(InteractionUsage? usage)
        => usage is null ? null : new ImageUsageData
        {
            InputTokens = usage.TotalInputTokens,
            OutputTokens = usage.TotalOutputTokens,
            TotalTokens = usage.TotalTokens
        };

    private static OpenAIImageUsage? ToOpenAIImageUsage(InteractionUsage? usage)
        => usage is null ? null : new OpenAIImageUsage
        {
            InputTokens = usage.TotalInputTokens,
            OutputTokens = usage.TotalOutputTokens,
            TotalTokens = usage.TotalTokens,
            InputTokensDetails = ToOpenAIImageTokenDetails(usage.InputTokensByModality),
            OutputTokensDetails = ToOpenAIImageTokenDetails(usage.OutputTokensByModality)
        };

    private static OpenAIImageTokenDetails? ToOpenAIImageTokenDetails(List<InteractionModalityTokens>? modalities)
    {
        if (modalities is null)
            return null;

        return new OpenAIImageTokenDetails
        {
            ImageTokens = modalities.Where(item => string.Equals(item.Modality, "image", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Tokens),
            TextTokens = modalities.Where(item => string.Equals(item.Modality, "text", StringComparison.OrdinalIgnoreCase)).Sum(item => item.Tokens)
        };
    }

    private sealed record GoogleInteractionImage(string Data, string MimeType);
}
