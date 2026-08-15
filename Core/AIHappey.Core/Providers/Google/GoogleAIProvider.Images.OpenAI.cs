using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Interactions;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.OutputFormat ??= "jpeg";
        options.ValidateOpenAIImageGenerationRequest();
        ValidateGoogleImageRequest(options.Model, options.Prompt);

        var images = new List<GoogleInteractionImage>();
        InteractionUsage? usage = null;
        string? created = null;
        var count = options.N ?? 1;
        for (var index = 0; index < count; index++)
        {
            var interaction = await GetInteraction(CreateGoogleImageInteractionRequest(
                options.Model,
                [new InteractionTextContent { Text = options.Prompt }],
                null,
                options.Size,
                options.OutputFormat), cancellationToken);

            images.AddRange(ExtractGoogleInteractionImages(interaction));
            usage = AddGoogleImageUsage(usage, interaction.Usage);
            created ??= interaction.Created;
        }

        return CreateOpenAIImagesResponse(images, usage, created, options.OutputFormat, options.Size, options.Quality, options.Background);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.OutputFormat ??= "jpeg";
        options.ValidateOpenAIImageGenerationRequest();
        ValidateGoogleImageRequest(options.Model, options.Prompt);

        for (var index = 0; index < (options.N ?? 1); index++)
        {
            await foreach (var streamEvent in StreamGoogleImageInteraction(
                CreateGoogleImageInteractionRequest(
                    options.Model,
                    [new InteractionTextContent { Text = options.Prompt }],
                    null,
                    options.Size,
                    options.OutputFormat),
                options.PartialImages ?? 0,
                isEdit: false,
                options.Size,
                options.Quality,
                options.Background,
                options.OutputFormat,
                cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.OutputFormat ??= "jpeg";
        options.ValidateOpenAIImageEditRequest();
        ValidateGoogleImageRequest(options.Model, options.Prompt);
        var input = await CreateGoogleEditInput(options, cancellationToken);

        var images = new List<GoogleInteractionImage>();
        InteractionUsage? usage = null;
        string? created = null;
        for (var index = 0; index < (options.N ?? 1); index++)
        {
            var interaction = await GetInteraction(CreateGoogleImageInteractionRequest(
                options.Model,
                input,
                null,
                options.Size,
                options.OutputFormat), cancellationToken);

            images.AddRange(ExtractGoogleInteractionImages(interaction));
            usage = AddGoogleImageUsage(usage, interaction.Usage);
            created ??= interaction.Created;
        }

        return CreateOpenAIImagesResponse(images, usage, created, options.OutputFormat, options.Size, options.Quality, options.Background);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.OutputFormat ??= "jpeg";
        options.ValidateOpenAIImageEditRequest();
        ValidateGoogleImageRequest(options.Model, options.Prompt);
        var input = await CreateGoogleEditInput(options, cancellationToken);

        for (var index = 0; index < (options.N ?? 1); index++)
        {
            await foreach (var streamEvent in StreamGoogleImageInteraction(
                CreateGoogleImageInteractionRequest(options.Model, input, null, options.Size, options.OutputFormat),
                options.PartialImages ?? 0,
                isEdit: true,
                options.Size,
                options.Quality,
                options.Background,
                options.OutputFormat,
                cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }

    private async IAsyncEnumerable<IOpenAIImageStreamEvent> StreamGoogleImageInteraction(
        InteractionRequest request,
        int partialImages,
        bool isEdit,
        string? size,
        string? quality,
        string? background,
        string? outputFormat,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var latestImages = new Dictionary<int, GoogleInteractionImage>();
        var emittedPartials = 0;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Interaction? completedInteraction = null;

        await foreach (var evt in GetInteractions(request, cancellationToken))
        {
            if (evt is InteractionErrorEvent error)
                throw new InvalidOperationException($"Google image stream failed: {error.Error?.Message ?? error.Error?.Code ?? "unknown error"}");

            if (evt is InteractionCreatedEvent created)
            {
                completedInteraction ??= created.Interaction;
                if (DateTimeOffset.TryParse(created.Interaction?.Created, out var timestamp))
                    createdAt = timestamp.ToUnixTimeSeconds();
            }

            if (evt is InteractionStepStartEvent { Step: InteractionImageContent image } start
                && !string.IsNullOrWhiteSpace(image.Data))
            {
                latestImages[start.Index] = new(image.Data.RemoveDataUrlPrefix(), image.MimeType ?? MediaTypeNames.Image.Png);
            }

            if (evt is InteractionStepDeltaEvent delta
                && string.Equals(delta.Delta?.Type, "image", StringComparison.OrdinalIgnoreCase)
                && TryGetGoogleImageDelta(delta, out var partial))
            {
                latestImages[delta.Index] = partial;
                if (emittedPartials < partialImages)
                {
                    yield return CreateOpenAIImageStreamEvent(isEdit, completed: false, partial.Data, emittedPartials++, createdAt,
                        size, quality, background, outputFormat, null);
                }
            }

            if (evt is InteractionCompletedEvent completed)
                completedInteraction = completed.Interaction;
        }

        var finalImages = completedInteraction is null ? [] : ExtractGoogleInteractionImages(completedInteraction);
        if (finalImages.Count == 0)
            finalImages = latestImages.OrderBy(item => item.Key).Select(item => item.Value).ToList();
        if (finalImages.Count == 0)
            throw new InvalidOperationException("Google image stream completed without a generated image.");

        var usage = ToOpenAIImageUsage(completedInteraction?.Usage);
        foreach (var image in finalImages)
        {
            yield return CreateOpenAIImageStreamEvent(isEdit, completed: true, image.Data, 0, createdAt,
                size, quality, background, outputFormat ?? MimeTypeToOutputFormat(image.MimeType), usage);
        }
    }

    private static async Task<List<InteractionContent>> CreateGoogleEditInput(OpenAIImageEditRequest options, CancellationToken cancellationToken)
    {
        var input = new List<InteractionContent> { new InteractionTextContent { Text = options.Prompt } };

        foreach (var image in options.Images ?? [])
        {
            if (string.IsNullOrWhiteSpace(image.ImageUrl))
                continue;
            input.Add(CreateGoogleImageReferenceContent(image.ImageUrl));
        }

        foreach (var file in options.ImageFiles ?? [])
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            input.Add(new InteractionImageContent
            {
                Data = Convert.ToBase64String(memory.ToArray()),
                MimeType = string.IsNullOrWhiteSpace(file.ContentType) ? MediaTypeNames.Image.Png : file.ContentType
            });
        }

        if (input.Count == 1)
            throw new ArgumentException("At least one input image is required for a Google image edit.", nameof(options));
        return input;
    }

    private static InteractionImageContent CreateGoogleImageReferenceContent(string value)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = value.IndexOf(',');
            var metadata = separator > 5 ? value[5..separator] : string.Empty;
            var mimeType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? MediaTypeNames.Image.Png;
            return new InteractionImageContent { Data = value.RemoveDataUrlPrefix(), MimeType = mimeType };
        }

        return new InteractionImageContent { Uri = value };
    }

    private static bool TryGetGoogleImageDelta(InteractionStepDeltaEvent delta, out GoogleInteractionImage image)
    {
        var data = GetGoogleDeltaString(delta, "data") ?? delta.Delta?.Text;
        if (string.IsNullOrWhiteSpace(data))
        {
            image = null!;
            return false;
        }

        image = new GoogleInteractionImage(
            data.RemoveDataUrlPrefix(),
            GetGoogleDeltaString(delta, "mime_type") ?? MediaTypeNames.Image.Png);
        return true;
    }

    private static string? GetGoogleDeltaString(InteractionStepDeltaEvent delta, string propertyName)
    {
        if (delta.Delta?.AdditionalProperties?.TryGetValue(propertyName, out var value) != true)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static OpenAIImagesResponse CreateOpenAIImagesResponse(
        List<GoogleInteractionImage> images,
        InteractionUsage? usage,
        string? created,
        string? outputFormat,
        string? size,
        string? quality,
        string? background)
    {
        if (images.Count == 0)
            throw new InvalidOperationException("Google image response did not contain a generated image.");

        return new OpenAIImagesResponse
        {
            Created = DateTimeOffset.TryParse(created, out var timestamp)
                ? timestamp.ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = images.Select(image => new OpenAIImageData { B64Json = image.Data }).ToList(),
            Usage = ToOpenAIImageUsage(usage),
            OutputFormat = outputFormat ?? MimeTypeToOutputFormat(images[0].MimeType),
            Size = size,
            Quality = quality,
            Background = background
        };
    }

    private static IOpenAIImageStreamEvent CreateOpenAIImageStreamEvent(
        bool isEdit,
        bool completed,
        string data,
        int partialIndex,
        long createdAt,
        string? size,
        string? quality,
        string? background,
        string? outputFormat,
        OpenAIImageUsage? usage)
    {
        OpenAIImageStreamEventBase result = (isEdit, completed) switch
        {
            (true, true) => new OpenAIImageEditCompleted { B64Json = data, Usage = usage },
            (true, false) => new OpenAIImageEditPartialImage { B64Json = data, PartialImageIndex = partialIndex },
            (false, true) => new OpenAIImageGenerationCompleted { B64Json = data, Usage = usage },
            _ => new OpenAIImageGenerationPartialImage { B64Json = data, PartialImageIndex = partialIndex }
        };

        result.CreatedAt = createdAt;
        result.Size = size;
        result.Quality = quality;
        result.Background = background;
        result.OutputFormat = outputFormat;
        return result;
    }

    private static InteractionUsage? AddGoogleImageUsage(InteractionUsage? left, InteractionUsage? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return new InteractionUsage
        {
            TotalInputTokens = (left.TotalInputTokens ?? 0) + (right.TotalInputTokens ?? 0),
            TotalOutputTokens = (left.TotalOutputTokens ?? 0) + (right.TotalOutputTokens ?? 0),
            TotalTokens = (left.TotalTokens ?? 0) + (right.TotalTokens ?? 0)
        };
    }

    private static string MimeTypeToOutputFormat(string mimeType)
        => string.Equals(mimeType, MediaTypeNames.Image.Jpeg, StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png";
}
