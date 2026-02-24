using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using ModelContextProtocol.Protocol;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.AI;

public static class ModelProviderImageExtensions
{
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
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }

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

        var header = input.Substring(5, commaIndex - 5); // strip "data:"
        var payload = input[(commaIndex + 1)..];

        string? mimeType = null;

        var semiIndex = header.IndexOf(';');
        if (semiIndex >= 0)
            mimeType = header.Substring(0, semiIndex);
        else if (!string.IsNullOrWhiteSpace(header))
            mimeType = header;

        return (string.IsNullOrWhiteSpace(mimeType) ? null : mimeType, payload.Trim());
    }
}
