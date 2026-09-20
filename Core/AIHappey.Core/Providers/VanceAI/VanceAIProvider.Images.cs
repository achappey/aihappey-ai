using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.VanceAI;

public partial class VanceAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeVanceAIModel(request.Model);
        var files = request.Files?.Where(file => !string.IsNullOrWhiteSpace(file.Data)).ToList() ?? [];
        if (files.Count != 1)
            throw new ArgumentException("VanceAI image tools require exactly one source image.", nameof(request));

        var source = files[0];
        if (string.Equals(source.Type, "file_id", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("VanceAI does not support image file IDs; provide image bytes or a public URL.", nameof(request));

        var config = BuildVanceAIConfig(request.ProviderOptions);
        if (string.Equals(model, "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("VanceAI custom requires a non-empty prompt.", nameof(request));
            config["prompt"] = JsonSerializer.SerializeToElement(request.Prompt);
        }

        var outputFormat = ReadVanceAIOptionString(request.ProviderOptions, "output_format", "outputFormat") ?? "jpg";
        var created = await CreateVanceAIImageJobAsync(source, model, config, outputFormat, cancellationToken);
        var current = created;
        while (!string.Equals(current.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(current.Status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(ReadVanceAIJobError(current));

            if (!ReferenceEquals(current, created))
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            current = await GetVanceAIJobAsync(created.JobId, cancellationToken);
        }

        var fallbackMediaType = outputFormat.ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Jpeg
        };
        var result = await DownloadVanceAIResultAsync(created.JobId, fallbackMediaType, cancellationToken);

        return new ImageResponse
        {
            Images = [$"data:{result.MediaType};base64,{Convert.ToBase64String(result.Bytes)}"],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(current.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var request = options.ToImageRequest(options.Model, GetIdentifier());
        AddVanceAIOutputFormat(request, options.OutputFormat);
        return (await ImageRequest(request, cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    OutputFormat = response.OutputFormat,
                    Size = response.Size
                };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var request = await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken);
        AddVanceAIOutputFormat(request, options.OutputFormat);
        return (await ImageRequest(request, cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    OutputFormat = response.OutputFormat,
                    Size = response.Size
                };
        }
    }

    private async Task<VanceAIJob> CreateVanceAIImageJobAsync(
        ImageFile source,
        string model,
        Dictionary<string, JsonElement> config,
        string outputFormat,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/jobs");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));

        if (IsHttpUrl(source.Data))
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    image_url = source.Data,
                    tool = model,
                    config,
                    output_format = outputFormat
                }, VanceAIJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }
        else
        {
            var bytes = DecodeMediaData(source.Data, out var dataUrlMediaType);
            var mediaType = dataUrlMediaType ?? source.MediaType ?? MediaTypeNames.Image.Jpeg;
            var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(bytes);
            image.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            form.Add(image, "image", $"image.{MediaTypeToImageExtension(mediaType)}");
            form.Add(new StringContent(model), "tool");
            form.Add(new StringContent(JsonSerializer.Serialize(config, VanceAIJsonOptions)), "config");
            form.Add(new StringContent(outputFormat), "output_format");
            request.Content = form;
        }

        return await SendVanceAIJobRequestAsync(request, "create image job", cancellationToken);
    }

    private void AddVanceAIOutputFormat(ImageRequest request, string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return;

        request.ProviderOptions ??= [];
        var options = GetVanceAIOptions(request.ProviderOptions);
        var values = options is { ValueKind: JsonValueKind.Object }
            ? options.Value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
            : new Dictionary<string, JsonElement>();
        values["output_format"] = JsonSerializer.SerializeToElement(outputFormat);
        request.ProviderOptions[GetIdentifier()] = JsonSerializer.SerializeToElement(values, VanceAIJsonOptions);
    }

    private static string MediaTypeToImageExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            MediaTypeNames.Image.Png => "png",
            "image/webp" => "webp",
            _ => "jpg"
        };
}
