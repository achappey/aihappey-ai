using System.Diagnostics;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ModelScope;

public sealed partial class ModelScopeProvider
{
    private static readonly TimeSpan ImageTaskTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InitialImagePollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumImagePollDelay = TimeSpan.FromSeconds(10);

    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var warnings = new List<object>();
        var files = request.Files?.ToArray() ?? [];
        if (files.Length > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Only one input image is supported." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "ModelScope returns one image per task." });

        var payload = new Dictionary<string, object?>();
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true &&
            options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
            {
                if (!property.NameEquals("model") && !property.NameEquals("prompt"))
                    payload[property.Name] = property.Value.Clone();
            }
        }

        payload["model"] = StripProviderPrefix(request.Model);
        payload["prompt"] = request.Prompt;
        if (request.Size is not null)
            payload["size"] = request.Size;
        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;
        if (files.Length > 0)
            payload["image_url"] = ToModelScopeImageUrl(files[0]);

        var apiKey = GetApiKey();
        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        submitRequest.Headers.Authorization = new("Bearer", apiKey);
        submitRequest.Headers.TryAddWithoutValidation("X-ModelScope-Async-Mode", "true");

        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitBody = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ModelScope image generation failed with status {(int)submitResponse.StatusCode}: {submitBody}",
                null,
                submitResponse.StatusCode);

        using var submitDocument = JsonDocument.Parse(submitBody);
        if (!submitDocument.RootElement.TryGetProperty("task_id", out var taskIdElement) ||
            taskIdElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(taskIdElement.GetString()))
            throw new InvalidOperationException("ModelScope image generation response did not contain a task_id.");

        var task = await PollImageTaskAsync(taskIdElement.GetString()!, apiKey, cancellationToken);
        if (!task.TryGetProperty("output_images", out var outputImages) ||
            outputImages.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ModelScope image task succeeded without output_images.");

        var images = new List<string>();
        foreach (var image in outputImages.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString()))
                continue;

            images.Add(await DownloadImageAsDataUrlAsync(image.GetString()!, cancellationToken));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("ModelScope image task succeeded without any image URLs.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<JsonElement> PollImageTaskAsync(
        string taskId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var delay = InitialImagePollDelay;
        while (elapsed.Elapsed < ImageTaskTimeout)
        {
            var remaining = ImageTaskTimeout - elapsed.Elapsed;
            await Task.Delay(delay < remaining ? delay : remaining, cancellationToken);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/tasks/{Uri.EscapeDataString(taskId)}");
            request.Headers.Authorization = new("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("X-ModelScope-Task-Type", "image_generation");

            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"ModelScope image task polling failed with status {(int)response.StatusCode}: {body}",
                    null,
                    response.StatusCode);

            using var document = JsonDocument.Parse(body);
            var task = document.RootElement.Clone();
            var status = GetTaskStatus(task);
            if (status.Equals("SUCCEED", StringComparison.OrdinalIgnoreCase))
                return task;

            if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ModelScope image task failed: {GetTaskError(task) ?? body}");

            delay = TimeSpan.FromSeconds(Math.Min(
                MaximumImagePollDelay.TotalSeconds,
                delay.TotalSeconds * 1.5));
        }

        throw new TimeoutException($"ModelScope image task '{taskId}' did not complete within {ImageTaskTimeout}.");
    }

    private async Task<string> DownloadImageAsDataUrlAsync(
        string imageUrl,
        CancellationToken cancellationToken)
    {
        if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return imageUrl;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("ModelScope returned an invalid image URL.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = null;
        using var response = await _mediaClient.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Downloading a ModelScope image failed with status {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType) ||
            !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            mediaType = MediaTypeNames.Image.Png;

        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? GetTaskStatus(JsonElement task)
        => task.TryGetProperty("task_status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()
            : null;

    private static string? GetTaskError(JsonElement task)
    {
        if (task.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();
        if (task.TryGetProperty("errors", out var errors))
        {
            if (errors.ValueKind == JsonValueKind.String)
                return errors.GetString();
            if (errors.ValueKind == JsonValueKind.Object &&
                errors.TryGetProperty("message", out var errorMessage) &&
                errorMessage.ValueKind == JsonValueKind.String)
                return errorMessage.GetString();
        }

        return null;
    }

    private string StripProviderPrefix(string model)
        => model.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
            ? model[(GetIdentifier().Length + 1)..]
            : model;

    private static string ToModelScopeImageUrl(ImageFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("The ModelScope input image must include image data or a URL.");
        if (file.Type.Equals("url", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        if (file.Data.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        var mediaType = file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            ? file.MediaType
            : MediaTypeNames.Image.Png;
        return $"data:{mediaType};base64,{file.Data}";
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        return (await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        return (await ImageRequest(
            await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken),
            cancellationToken)).ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var response = await ImageRequest(
            await options.ToImageRequest(options.Model, GetIdentifier(), cancellationToken),
            cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageEditCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }
}
