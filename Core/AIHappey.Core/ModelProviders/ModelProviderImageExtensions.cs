using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using ModelContextProtocol.Protocol;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.AI;

public static class ModelProviderImageExtensions
{
    public static Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        this IModelProvider modelProvider,
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public static IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        this IModelProvider modelProvider,
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public static Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        this IModelProvider modelProvider,
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public static IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        this IModelProvider modelProvider,
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public static Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(
        this IModelProvider modelProvider,
        OpenAIImageVariationRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public static async IAsyncEnumerable<UIMessagePart> StreamImageAsync(this IModelProvider modelProvider,
      ChatRequest chatRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", chatRequest.Messages?
            .LastOrDefault(m => m.Role == Vercel.Models.Role.user)
            ?.Parts?.OfType<TextUIPart>().Select(a => a.Text) ?? []);

        var inputFiles = chatRequest.Messages?
            .LastOrDefault(m => m.Role == Vercel.Models.Role.user)
            ?.Parts?.GetImages()
                .Select(a => a.ToImageFile()) ?? [];

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();

            yield break;
        }

        // 2. Build ImageRequest
        var imageRequest = new ImageRequest
        {
            Prompt = prompt,
            Model = chatRequest.Model,
            Files = inputFiles,
            ProviderOptions = chatRequest.ProviderMetadata
        };

        ImageResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await modelProvider.ImageRequest(imageRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
        }

        if (!string.IsNullOrEmpty(exceptionMessage))
        {
            yield return exceptionMessage.ToErrorUIPart();
            yield break;
        }

        foreach (var image in result?.Images ?? [])
        {
            var commaIndex = image.IndexOf(',');

            if (commaIndex <= 0)
                continue;

            var header = image[..commaIndex];              // data:image/png;base64
            var data = image[(commaIndex + 1)..];          // base64 payload

            var mediaType = header
                .Replace("data:", "", StringComparison.OrdinalIgnoreCase)
                .Replace(";base64", "", StringComparison.OrdinalIgnoreCase);

            yield return new FileUIPart
            {
                MediaType = mediaType,   // "image/png"
                Url = data              // keep full data URL
            };
        }

        // 4. Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model.ToModelId(modelProvider.GetIdentifier()),
             0, 0, 0, null);
    }

    [Obsolete("MCP Sampling obsolete")]
    public static async Task<CreateMessageResult> ImageSamplingAsync(
              this IModelProvider modelProvider,
              CreateMessageRequestParams chatRequest,
              CancellationToken cancellationToken = default)
    {
        var input = string.Join("\n\n", chatRequest
            .Messages
            .Where(a => a.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(z => z.Content.OfType<TextContentBlock>())
            .Select(a => a.Text));

        var inputImages = chatRequest
                .Messages
                .Where(a => a.Role == ModelContextProtocol.Protocol.Role.User)
                .SelectMany(z => z.Content.OfType<ImageContentBlock>());

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new Exception("No prompt provided.");
        }

        var model = chatRequest.GetModel();

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new Exception("No model provided.");
        }

        var imageRequest = new ImageRequest
        {
            Model = model,
            Prompt = input,
            ProviderOptions = chatRequest.Metadata?
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => JsonSerializer.SerializeToElement(kvp.Value)
                ),
            Files = inputImages.Select(a => new ImageFile()
            {
                MediaType = a.MimeType,
                Data = Convert.ToBase64String(a.Data.ToArray())
            })
        };

        var result = await modelProvider.ImageRequest(imageRequest, cancellationToken) ?? throw new Exception("No result.");

        return result.ToCreateMessageResult();
    }

    public static ImageContentBlock ToImageContentBlock(
        this string image)
             => new()
             {
                 MimeType = image.ExtractMimeTypeAndBase64().MimeType!,
                 Data = Convert.FromBase64String(image.ExtractMimeTypeAndBase64().Base64!)
             };

    public static CreateMessageResult ToCreateMessageResult(
        this ImageResponse result)
        => new()
        {
            Content = [.. result.Images?.Select(a => a.ToImageContentBlock()).ToList() ?? []],
            Model = result.Response.ModelId
        };

    /// <summary>
    /// Extracts MIME type and raw Base64 from a data URL or prefixed string.
    /// Example input: "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAA..."
    /// </summary>
    public static (string? MimeType, string Base64) ExtractMimeTypeAndBase64(this string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Input cannot be null or empty.", nameof(input));

        // Not a data URL → assume whole string is already raw base64
        if (!input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return (null, input.Trim());

        // data:[mime];base64,<payload>
        var commaIndex = input.IndexOf(',');
        if (commaIndex < 0)
            throw new FormatException("Invalid data URL: missing comma.");

        var header = input[5..commaIndex]; // strip "data:"
        var payload = input[(commaIndex + 1)..];

        string? mimeType = null;

        var semiIndex = header.IndexOf(';');
        if (semiIndex >= 0)
            mimeType = header[..semiIndex];
        else if (!string.IsNullOrWhiteSpace(header))
            mimeType = header;

        return (string.IsNullOrWhiteSpace(mimeType) ? null : mimeType, payload.Trim());
    }

    public static async Task<ResponseResult> ImageResponseAsync(
       this IModelProvider modelProvider,
       ResponseRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        var input = chatRequest.Input?.IsText == true ?
            chatRequest.Input.Text : chatRequest.Input?.Items?
            .OfType<ResponseInputMessage>()
            .LastOrDefault()?.Content.Text;

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new Exception("No prompt provided.");
        }

        var imageRequest = new ImageRequest
        {
            Model = chatRequest.Model!,
            Prompt = input
        };

        ImageResponse? result;
        try
        {
            result = await modelProvider.ImageRequest(imageRequest, cancellationToken);
        }
        catch (Exception e)
        {
            return new ResponseResult()
            {
                Id = Guid.NewGuid().ToString(),
                Error = new ResponseResultError()
                {
                    Code = "500",
                    Message = e.Message
                }
            };
        }

        if (result == null)
        {
            return new ResponseResult()
            {
                Id = Guid.NewGuid().ToString(),
                Error = new ResponseResultError()
                {
                    Code = "500",
                    Message = "No response"
                }
            };

        }

        //var audio = result?.Audio as string;
        if (result?.Images?.Any() != true)
        {
            return new ResponseResult()
            {
                Id = Guid.NewGuid().ToString(),
                Error = new ResponseResultError()
                {
                    Code = "500",
                    Message = "No images"
                }
            };
        }

        return new ResponseResult()
        {
            Id = Guid.NewGuid().ToString(),
            Model = result.Response.ModelId,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CreatedAt = new DateTimeOffset(result.Response.Timestamp)
                .ToUnixTimeSeconds(),
            Output =
             [
                 .. result.Images.Select(image =>
                     new ResponseImageGenerationCallItem
                     {
                         Id = $"ig_{Guid.NewGuid():N}",
                         Status = "completed",
                         Result = NormalizeImageBase64(image)
                     })
             ]
        };
    }

    private static string NormalizeImageBase64(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException("Image result was empty.");

        // OpenAI expects raw base64 in image_generation_call.result,
        // not a data:image/png;base64,... URL.
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = image.IndexOf(',');

            if (commaIndex < 0 || commaIndex == image.Length - 1)
                throw new InvalidOperationException("Invalid image data URL.");

            return image[(commaIndex + 1)..];
        }

        return image;
    }



    public static async IAsyncEnumerable<ResponseStreamPart>
        ImageResponsesStreamingAsync(
            this IModelProvider modelProvider,
            ResponseRequest options,
            [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var prompt = options.Input?.IsText == true
            ? options.Input.Text
            : options.Input?.Items?
                .OfType<ResponseInputMessage>()
                .LastOrDefault()?
                .Content.Text;

        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("No prompt provided.");

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("No model provided.");

        var responseId = $"resp_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequenceNumber = 0;

        var inProgressResponse = new ResponseResult
        {
            Id = responseId,
            Model = options.Model,
            CreatedAt = createdAt,
            Status = "in_progress",
            Output = []
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequenceNumber++,
            Response = inProgressResponse
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequenceNumber++,
            Response = inProgressResponse
        };

        var imageRequest = new ImageRequest
        {
            Model = options.Model,
            Prompt = prompt
        };

        ImageResponse? result = null;
        Exception? exception = null;

        try
        {
            result = await modelProvider.ImageRequest(
                imageRequest,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            exception = e;
        }

        if (exception != null)
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequenceNumber++,
                Response = CreateFailedImageResponse(
                    responseId,
                    options.Model,
                    createdAt,
                    exception.Message)
            };

            yield break;
        }

        if (result == null)
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequenceNumber++,
                Response = CreateFailedImageResponse(
                    responseId,
                    options.Model,
                    createdAt,
                    "No response")
            };

            yield break;
        }

        var images = result.Images?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeImageBase64)
            .ToArray();

        if (images?.Length is not > 0)
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequenceNumber++,
                Response = CreateFailedImageResponse(
                    responseId,
                    options.Model,
                    createdAt,
                    "No images")
            };

            yield break;
        }

        var outputItems = images
            .Select(image => new ResponseImageGenerationCallItem
            {
                Id = $"ig_{Guid.NewGuid():N}",
                Status = "completed",
                Result = image
            })
            .ToArray();

        for (var outputIndex = 0;
             outputIndex < outputItems.Length;
             outputIndex++)
        {
            var outputItem = outputItems[outputIndex];

            yield return new ResponseOutputItemAdded
            {
                SequenceNumber = sequenceNumber++,
                OutputIndex = outputIndex,
                Item = new ResponseStreamItem
                {
                    Id = outputItem.Id,
                    Type = "image_generation_call",
                    Status = "in_progress"
                }
            };

            yield return new ResponseImageGenerationCallInProgress
            {
                SequenceNumber = sequenceNumber++,
                OutputIndex = outputIndex,
                ItemId = outputItem.Id!
            };

            yield return new ResponseImageGenerationCallGenerating
            {
                SequenceNumber = sequenceNumber++,
                OutputIndex = outputIndex,
                ItemId = outputItem.Id!
            };

            /*
             * No partial_image event here.
             *
             * modelProvider.ImageRequest currently returns complete images,
             * so emitting the final image as a fake partial image would be wrong.
             */

            yield return new ResponseImageGenerationCallGenerating
            {
                SequenceNumber = sequenceNumber++,
                OutputIndex = outputIndex,
                ItemId = outputItem.Id!
            };

            yield return new ResponseOutputItemDone
            {
                SequenceNumber = sequenceNumber++,
                OutputIndex = outputIndex,
                Item = new ResponseStreamItem
                {
                    Id = outputItem.Id!,
                    Type = "image_generation_call",
                    Status = "completed",
                    AdditionalProperties = new Dictionary<string, JsonElement>()
                    {
                        {"result", JsonSerializer.SerializeToElement(outputItem.Result,
                            JsonSerializerOptions.Web)}
                    }
                }
            };
        }

        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var completedResponse = new ResponseResult
        {
            Id = responseId,
            Model = string.IsNullOrWhiteSpace(result.Response.ModelId)
                ? options.Model
                : result.Response.ModelId,
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = "completed",
            Output = outputItems
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequenceNumber++,
            Response = completedResponse
        };
    }

    private static ResponseResult CreateFailedImageResponse(
        string responseId,
        string model,
        long createdAt,
        string message)
    {
        return new ResponseResult
        {
            Id = responseId,
            Model = model,
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = "failed",
            Output = [],
            Error = new ResponseResultError
            {
                Code = "image_generation_error",
                Message = message
            }
        };
    }


}
