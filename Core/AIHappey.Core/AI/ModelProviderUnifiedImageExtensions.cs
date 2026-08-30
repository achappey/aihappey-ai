using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

/// <summary>
/// Adapts provider image generation and editing operations to the unified
/// conversation contract as a synthetic tool call that includes its output.
/// </summary>
public static class ModelProviderUnifiedImageExtensions
{
    internal const string ToolName = "generate_image";
    private const int DefaultPartialImageCount = 3;

    public static async Task<bool> IsImageModelAsync(
        this IModelProvider modelProvider,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var model = await modelProvider.GetModel(modelId, cancellationToken);
        return string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AIResponse> ExecuteUnifiedImageAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = modelProvider.GetIdentifier();
        var imageRequest = CreateImageRequest(request, providerId);
        var toolCallId = CreateToolCallId(request);
        var response = await modelProvider.ImageRequest(imageRequest, cancellationToken);
        var images = NormalizeImages(response.Images).ToList();

        if (images.Count == 0)
            throw new InvalidOperationException("The image provider completed without returning an image.");

        return new AIResponse
        {
            ProviderId = providerId,
            Model = request.Model,
            Status = "completed",
            Usage = ToUnifiedUsage(response.Usage),
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-call",
                                ToolCallId = toolCallId,
                                ToolName = ToolName,
                                Title = imageRequest.Files?.Any() == true ? "Edit image" : "Generate image",
                                Input = CreateToolInput(imageRequest),
                                State = "output-available",
                                Output = CreateImageCallToolResult(
                                    images,
                                    status: "completed",
                                    operation: imageRequest.Files?.Any() == true ? "edit" : "generation",
                                    warnings: response.Warnings,
                                    usage: response.Usage),
                                ProviderExecuted = true,
                                Metadata = CreateToolMetadata(providerId, response.ProviderMetadata)
                            }
                        ]
                    }
                ]
            }
        };
    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedImageAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = modelProvider.GetIdentifier();
        var imageRequest = CreateImageRequest(request, providerId);
        var isEdit = imageRequest.Files?.Any() == true;
        var operation = isEdit ? "edit" : "generation";
        var toolCallId = CreateToolCallId(request);
        var toolInput = CreateToolInput(imageRequest);

        yield return CreateEvent(providerId, "tool-input-available", toolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = ToolName,
                Title = isEdit ? "Edit image" : "Generate image",
                Input = toolInput,
                ProviderExecuted = true
            });

        var completedImages = new List<NormalizedImage>();
        OpenAIImageUsage? usage = null;
        var receivedPartialImage = false;
        Exception? streamError = null;

        var stream = isEdit
            ? modelProvider.OpenAIImageEditStreamingAsync(
                CreateOpenAIEditRequest(imageRequest, request.Metadata, stream: true),
                cancellationToken)
            : modelProvider.OpenAIImageGenerationStreamingAsync(
                CreateOpenAIGenerationRequest(imageRequest, request.Metadata, stream: true),
                cancellationToken);

        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (streamError is null)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                streamError = ex;
                break;
            }

            if (!hasNext)
                break;

            AIStreamEvent? partialOutputEvent = null;
            try
            {
                switch (enumerator.Current)
                {
                    case OpenAIImageGenerationPartialImage partial:
                        receivedPartialImage = true;
                        partialOutputEvent = CreatePartialOutputEvent(
                            providerId,
                            toolCallId,
                            operation,
                            partial.B64Json,
                            partial.OutputFormat,
                            partial.PartialImageIndex);
                        break;

                    case OpenAIImageEditPartialImage partial:
                        receivedPartialImage = true;
                        partialOutputEvent = CreatePartialOutputEvent(
                            providerId,
                            toolCallId,
                            operation,
                            partial.B64Json,
                            partial.OutputFormat,
                            partial.PartialImageIndex);
                        break;

                    case OpenAIImageGenerationCompleted completed:
                        completedImages.Add(NormalizeImage(completed.B64Json, completed.OutputFormat));
                        usage ??= completed.Usage;
                        break;

                    case OpenAIImageEditCompleted completed:
                        completedImages.Add(NormalizeImage(completed.B64Json, completed.OutputFormat));
                        usage ??= completed.Usage;
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unsupported image stream event '{enumerator.Current?.GetType().Name ?? "null"}'.");
                }
            }
            catch (Exception ex)
            {
                streamError = ex;
            }

            if (partialOutputEvent is not null)
                yield return partialOutputEvent;
        }

        if (streamError is null && completedImages.Count == 0)
            streamError = new InvalidOperationException("The image stream ended without a completed image.");

        if (streamError is not null)
        {
            yield return CreateEvent(providerId, "tool-output-error", toolCallId,
                new AIToolOutputErrorEventData
                {
                    ToolCallId = toolCallId,
                    ErrorText = streamError.Message,
                    ProviderExecuted = true
                });
            yield return CreateFinishEvent(providerId, request.Model, toolCallId, "error", null);
            yield break;
        }

        if (!receivedPartialImage)
        {
            for (var index = 0; index < completedImages.Count; index++)
            {
                yield return CreateEvent(providerId, "tool-output-available", toolCallId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = ToolName,
                        Output = CreateImageCallToolResult(
                            [completedImages[index]],
                            status: "streaming",
                            operation: operation,
                            partialImageIndex: index),
                        ProviderExecuted = true,
                        Preliminary = true
                    });
            }
        }

        yield return CreateEvent(providerId, "tool-output-available", toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = ToolName,
                Output = CreateImageCallToolResult(
                    completedImages,
                    status: "completed",
                    operation: operation,
                    usage: usage),
                ProviderExecuted = true,
                Preliminary = false
            });

        yield return CreateFinishEvent(providerId, request.Model, toolCallId, "tool-calls", usage);
    }

    private static AIStreamEvent CreatePartialOutputEvent(
        string providerId,
        string toolCallId,
        string operation,
        string base64,
        string? outputFormat,
        int partialImageIndex)
        => CreateEvent(providerId, "tool-output-available", toolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = ToolName,
                Output = CreateImageCallToolResult(
                    [NormalizeImage(base64, outputFormat)],
                    status: "streaming",
                    operation: operation,
                    partialImageIndex: partialImageIndex),
                ProviderExecuted = true,
                Preliminary = true
            });

    private static ImageRequest CreateImageRequest(AIRequest request, string providerId)
    {
        var model = request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var lastUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var prompt = string.Join("\n", lastUserMessage?.Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Image conversation requests require a text prompt in the last user message.", nameof(request));

        var files = lastUserMessage?.Content?
            .OfType<AIFileContentPart>()
            .Where(IsImageFile)
            .Select(ToImageFile)
            .ToList() ?? [];

        return new ImageRequest
        {
            Model = GetProviderModelId(model, providerId),
            Prompt = prompt,
            Size = ReadMetadataString(request.Metadata, "size"),
            AspectRatio = ReadMetadataString(request.Metadata, "aspect_ratio", "aspectRatio"),
            Seed = ReadMetadataInt(request.Metadata, "seed"),
            N = ReadMetadataInt(request.Metadata, "n"),
            Files = files.Count == 0 ? null : files,
            ProviderOptions = ToProviderOptions(request.Metadata)
        };
    }

    private static OpenAIImageGenerationRequest CreateOpenAIGenerationRequest(
        ImageRequest request,
        Dictionary<string, object?>? metadata,
        bool stream)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            N = request.N,
            Size = request.Size,
            Background = ReadMetadataString(metadata, "background"),
            Moderation = ReadMetadataString(metadata, "moderation"),
            OutputCompression = ReadMetadataInt(metadata, "output_compression", "outputCompression"),
            OutputFormat = ReadMetadataString(metadata, "output_format", "outputFormat") ?? "png",
            PartialImages = stream
                ? ReadMetadataInt(metadata, "partial_images", "partialImages") ?? DefaultPartialImageCount
                : null,
            Quality = ReadMetadataString(metadata, "quality"),
            ResponseFormat = "b64_json",
            Stream = stream,
            Style = ReadMetadataString(metadata, "style"),
            User = ReadMetadataString(metadata, "user")
        };

    private static OpenAIImageEditRequest CreateOpenAIEditRequest(
        ImageRequest request,
        Dictionary<string, object?>? metadata,
        bool stream)
        => new()
        {
            Model = request.Model,
            Prompt = request.Prompt,
            Images = request.Files?.Select(ToOpenAIImageReference).ToArray(),
            N = request.N,
            Size = request.Size,
            Background = ReadMetadataString(metadata, "background"),
            InputFidelity = ReadMetadataString(metadata, "input_fidelity", "inputFidelity"),
            Moderation = ReadMetadataString(metadata, "moderation"),
            OutputCompression = ReadMetadataInt(metadata, "output_compression", "outputCompression"),
            OutputFormat = ReadMetadataString(metadata, "output_format", "outputFormat") ?? "png",
            PartialImages = stream
                ? ReadMetadataInt(metadata, "partial_images", "partialImages") ?? DefaultPartialImageCount
                : null,
            Quality = ReadMetadataString(metadata, "quality"),
            Stream = stream,
            User = ReadMetadataString(metadata, "user")
        };

    private static bool IsImageFile(AIFileContentPart file)
        => file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
           && file.Data is not null;

    private static ImageFile ToImageFile(AIFileContentPart file)
    {
        var data = file.Data?.ToString();
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentException("Image attachments must contain base64 data or a URL.");

        return new ImageFile
        {
            Type = Uri.TryCreate(data, UriKind.Absolute, out var uri)
                   && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? "url"
                : "file",
            MediaType = file.MediaType ?? "image/png",
            Data = data
        };
    }

    private static OpenAIImageReference ToOpenAIImageReference(ImageFile file)
    {
        if (string.Equals(file.Type, "file_id", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable CS0618
            return new OpenAIImageReference { FileId = file.Data };
#pragma warning restore CS0618
        }

        var imageUrl = string.Equals(file.Type, "url", StringComparison.OrdinalIgnoreCase)
                       || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data
            : $"data:{file.MediaType};base64,{file.Data}";
        return new OpenAIImageReference { ImageUrl = imageUrl };
    }

    private static object CreateToolInput(ImageRequest request)
        => new
        {
            prompt = request.Prompt,
            model = request.Model,
            size = request.Size,
            aspectRatio = request.AspectRatio,
            seed = request.Seed,
            n = request.N,
            attachmentCount = request.Files?.Count() ?? 0
        };

    private static CallToolResult CreateImageCallToolResult(
        IEnumerable<NormalizedImage> images,
        string status,
        string operation,
        IEnumerable<object>? warnings = null,
        object? usage = null,
        int? partialImageIndex = null)
    {
        var normalized = images.ToList();
        return new CallToolResult
        {
            IsError = false,
            Content = normalized.Select(image =>
                (ContentBlock)ImageContentBlock.FromBytes(image.Bytes, image.MediaType)).ToList(),
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                status,
                operation,
                partialImageIndex,
                imageCount = normalized.Count,
                outputFormat = normalized.FirstOrDefault()?.OutputFormat,
                warnings,
                usage
            }, JsonSerializerOptions.Web)
        };
    }

    private static IEnumerable<NormalizedImage> NormalizeImages(IEnumerable<string>? images)
    {
        foreach (var image in images ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image))
                yield return NormalizeImage(image, null);
        }
    }

    private static NormalizedImage NormalizeImage(string image, string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException("The image provider returned empty image data.");

        var mediaType = NormalizeMediaType(outputFormat) ?? "image/png";
        var base64 = image.Trim();
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = base64.IndexOf(',');
            if (commaIndex <= "data:".Length || commaIndex == base64.Length - 1)
                throw new InvalidOperationException("The image provider returned an invalid data URL.");

            var header = base64["data:".Length..commaIndex];
            var separator = header.IndexOf(';');
            var headerMediaType = separator >= 0 ? header[..separator] : header;
            if (headerMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = headerMediaType;
            base64 = base64[(commaIndex + 1)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The image provider returned malformed base64 image data.", ex);
        }

        if (bytes.Length == 0)
            throw new InvalidOperationException("The image provider returned empty image data.");

        return new NormalizedImage(bytes, mediaType, mediaType["image/".Length..]);
    }

    private static string? NormalizeMediaType(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;
        return outputFormat.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? outputFormat
            : $"image/{outputFormat.Trim().ToLowerInvariant()}";
    }

    private static AIUsage? ToUnifiedUsage(ImageUsageData? usage)
        => usage is null ? null : new AIUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens
        };

    private static AIUsage? ToUnifiedUsage(OpenAIImageUsage? usage)
        => usage is null ? null : new AIUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.TotalTokens
        };

    private static Dictionary<string, JsonElement>? ToProviderOptions(Dictionary<string, object?>? metadata)
        => metadata?.Where(entry => entry.Value is not null).ToDictionary(
            entry => entry.Key,
            entry => entry.Value is JsonElement json
                ? json.Clone()
                : JsonSerializer.SerializeToElement(entry.Value, JsonSerializerOptions.Web));

    private static Dictionary<string, object?> CreateToolMetadata(
        string providerId,
        Dictionary<string, JsonElement>? metadata)
        => new()
        {
            [providerId] = metadata?.ToDictionary(entry => entry.Key, entry => (object)entry.Value.Clone()) ?? []
        };

    private static string? ReadMetadataString(
        Dictionary<string, object?>? metadata,
        params string[] keys)
    {
        var value = ReadMetadataValue(metadata, keys);
        return value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement json when json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined => json.ToString(),
            _ => value.ToString()
        };
    }

    private static int? ReadMetadataInt(
        Dictionary<string, object?>? metadata,
        params string[] keys)
    {
        var value = ReadMetadataValue(metadata, keys);
        return value switch
        {
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number,
            JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var number) => number,
            string text when int.TryParse(text, out var number) => number,
            _ => null
        };
    }

    private static object? ReadMetadataValue(
        Dictionary<string, object?>? metadata,
        params string[] keys)
    {
        if (metadata is null)
            return null;

        foreach (var key in keys)
        {
            var direct = metadata.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(direct.Key))
                return direct.Value;
        }

        foreach (var value in metadata.Values)
        {
            if (value is JsonElement { ValueKind: JsonValueKind.Object } json)
            {
                foreach (var property in json.EnumerateObject())
                {
                    if (keys.Any(key => string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)))
                        return property.Value.Clone();
                }
            }

            if (value is IEnumerable<KeyValuePair<string, object?>> dictionary)
            {
                foreach (var entry in dictionary)
                {
                    if (keys.Any(key => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)))
                        return entry.Value;
                }
            }
        }

        return null;
    }

    private static AIStreamEvent CreateFinishEvent(
        string providerId,
        string? model,
        string id,
        string reason,
        OpenAIImageUsage? usage)
        => CreateEvent(providerId, "finish", id, new AIFinishEventData
        {
            FinishReason = reason,
            Model = model,
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
            TotalTokens = usage?.TotalTokens
        });

    private static AIStreamEvent CreateEvent(string providerId, string type, string id, object data)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data
            }
        };

    private static string CreateToolCallId(AIRequest request)
        => $"ig_{request.Id ?? Guid.NewGuid().ToString("N")}";

    private static string GetProviderModelId(string model, string providerId)
    {
        var split = model.SplitModelId();
        return string.Equals(split.Provider, providerId, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(split.Model)
            ? split.Model
            : model;
    }

    private sealed record NormalizedImage(byte[] Bytes, string MediaType, string OutputFormat);
}
