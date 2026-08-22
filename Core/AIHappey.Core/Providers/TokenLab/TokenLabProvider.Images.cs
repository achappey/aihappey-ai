using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TokenLab;

public partial class TokenLabProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateImageInput(request.Model, request.Prompt);

        var payload = CreateTokenLabPayload(GetTokenLabProviderOptions(request.ProviderOptions));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed;
        if (request.N is not null) payload["n"] = request.N;
        if (request.Files?.Any() == true)
            payload["images"] = new JsonArray(request.Files.Select(file => (JsonNode?)$"data:{file.MediaType};base64,{file.Data}").ToArray());
        if (request.Mask is not null)
            payload["mask"] = $"data:{request.Mask.MediaType};base64,{request.Mask.Data}";

        var endpoint = request.Files?.Any() == true ? "v1/images/edits" : "v1/images/generations";
        var create = await SendTokenLabJsonAsync(HttpMethod.Post, endpoint, ToJsonContent(payload), "image request", cancellationToken);
        var completed = await AwaitTokenLabTaskAsync(create.Root, create.Headers, "image request", cancellationToken);
        var images = await GetTokenLabImagesAsDataUrlsAsync(completed.Root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException($"TokenLab image request returned no images: {completed.Root.GetRawText()}");

        return new ImageResponse
        {
            Images = images,
            ProviderMetadata = CreateTokenLabMetadata(new { endpoint, create = create.Root, result = completed.Root }),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = completed.Headers
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateImageInput(options.Model, options.Prompt);
        var payload = JsonSerializer.SerializeToNode(options, TokenLabJson)!.AsObject();
        payload.Remove("stream");
        CopyAdditionalProperties(payload, options.AdditionalProperties);
        return await SendOpenAIImageJsonAsync(payload, "v1/images/generations", options.OutputFormat, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Size = response.Size,
                    Quality = response.Quality,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Usage = response.Usage
                };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateImageInput(options.Model, options.Prompt);

        if (options.ImageFiles?.Length > 0 || options.MaskFile is not null)
        {
            using var form = new MultipartFormDataContent();
            AddFormValue(form, "model", options.Model);
            AddFormValue(form, "prompt", options.Prompt);
            AddFormValue(form, "background", options.Background);
            AddFormValue(form, "input_fidelity", options.InputFidelity);
            AddFormValue(form, "moderation", options.Moderation);
            AddFormValue(form, "n", options.N);
            AddFormValue(form, "output_compression", options.OutputCompression);
            AddFormValue(form, "output_format", options.OutputFormat);
            AddFormValue(form, "partial_images", options.PartialImages);
            AddFormValue(form, "quality", options.Quality);
            AddFormValue(form, "size", options.Size);
            AddFormValue(form, "user", options.User);
            AddAdditionalFormValues(form, options.AdditionalProperties);
            foreach (var file in options.ImageFiles ?? [])
                form.Add(ToFileContent(file.ContentType, file.OpenReadStream()), "image", file.FileName);
            if (options.MaskFile is not null)
                form.Add(ToFileContent(options.MaskFile.ContentType, options.MaskFile.OpenReadStream()), "mask", options.MaskFile.FileName);

            var create = await SendTokenLabJsonAsync(HttpMethod.Post, "v1/images/edits", form, "image edit", cancellationToken);
            var completed = await AwaitTokenLabTaskAsync(create.Root, create.Headers, "image edit", cancellationToken);
            return await ToOpenAIImagesResponseAsync(completed.Root, options.OutputFormat, cancellationToken);
        }

        var payload = JsonSerializer.SerializeToNode(options, TokenLabJson)!.AsObject();
        payload.Remove("stream");
        CopyAdditionalProperties(payload, options.AdditionalProperties);
        return await SendOpenAIImageJsonAsync(payload, "v1/images/edits", options.OutputFormat, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted
                {
                    B64Json = image.B64Json,
                    CreatedAt = response.Created,
                    Size = response.Size,
                    Quality = response.Quality,
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Usage = response.Usage
                };
    }

    private async Task<OpenAIImagesResponse> SendOpenAIImageJsonAsync(JsonObject payload, string endpoint, string? outputFormat, CancellationToken cancellationToken)
    {
        var create = await SendTokenLabJsonAsync(HttpMethod.Post, endpoint, ToJsonContent(payload), "image request", cancellationToken);
        var completed = await AwaitTokenLabTaskAsync(create.Root, create.Headers, "image request", cancellationToken);
        return await ToOpenAIImagesResponseAsync(completed.Root, outputFormat, cancellationToken);
    }

    private async Task<OpenAIImagesResponse> ToOpenAIImagesResponseAsync(JsonElement root, string? outputFormat, CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Deserialize<OpenAIImagesResponse>(root.GetRawText(), TokenLabJson) ?? new OpenAIImagesResponse();
        var values = GetTokenLabMediaValues(root, "image");
        var data = new List<OpenAIImageData>();
        foreach (var value in values)
        {
            var media = await ResolveTokenLabMediaAsync(value, GetImageMimeType(outputFormat), cancellationToken);
            data.Add(new OpenAIImageData { B64Json = Convert.ToBase64String(media.Bytes) });
        }
        response.Created = response.Created == 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : response.Created;
        response.OutputFormat ??= outputFormat;
        response.Data = data.Count > 0 ? data : response.Data;
        if (response.Data?.Count is not > 0)
            throw new InvalidOperationException($"TokenLab image request returned no images: {root.GetRawText()}");
        return response;
    }

    private async Task<List<string>> GetTokenLabImagesAsDataUrlsAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var value in GetTokenLabMediaValues(root, "image"))
        {
            var media = await ResolveTokenLabMediaAsync(value, "image/png", cancellationToken);
            result.Add($"data:{media.MimeType};base64,{Convert.ToBase64String(media.Bytes)}");
        }
        return result;
    }

    private static void ValidateImageInput(string model, string prompt)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.");
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.");
    }

    private static void AddFormValue(MultipartFormDataContent form, string name, object? value)
    {
        if (value is not null)
            form.Add(new StringContent(Convert.ToString(value, CultureInfo.InvariantCulture)!), name);
    }

    private static void AddAdditionalFormValues(MultipartFormDataContent form, Dictionary<string, JsonElement>? values)
    {
        if (values is null) return;
        foreach (var value in values)
            form.Add(new StringContent(value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText()), value.Key);
    }

    private static StreamContent ToFileContent(string? contentType, Stream stream)
    {
        var content = new StreamContent(stream);
        if (!string.IsNullOrWhiteSpace(contentType)) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static string GetImageMimeType(string? format) => format?.ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => "image/jpeg",
        "webp" => "image/webp",
        _ => "image/png"
    };
}
