using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NavyAI;

public partial class NavyAIProvider
{
    private static readonly JsonSerializerOptions NavyImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var payload = NavyCopyOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        payload["sync"] = true;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        var files = request.Files?.ToArray() ?? [];
        if (files.Length > 0) payload["image_url"] = files.Length == 1 ? NavyImageValue(files[0]) : files.Select(NavyImageValue).Take(5).ToArray();

        var task = await CreateAndPollNavyImageAsync(payload, cancellationToken);
        var images = await ResolveNavyMediaAsync(task.Root, false, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("NavyAI image generation returned no images.");
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n", details = "NavyAI returns one image per request." });
        if (request.Mask is not null) warnings.Add(new { type = "unsupported", feature = "mask" });
        if (files.Length > 5) warnings.Add(new { type = "unsupported", feature = "files", details = "Only the first five reference images were sent." });
        return new ImageResponse
        {
            Images = images.Select(x => $"data:{x.MediaType};base64,{x.Base64}"),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(task.Root),
            Response = new HeaderResponseData
            {
                Timestamp = NavyCreated(task.Root),
                Headers = task.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = NavyCopyOptions(options.AdditionalProperties);
        payload["model"] = options.Model; payload["prompt"] = options.Prompt;
        payload["response_format"] = "b64_json"; payload["sync"] = true;
        if (!string.IsNullOrWhiteSpace(options.Size)) payload["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.Quality)) payload["quality"] = options.Quality;
        if (!string.IsNullOrWhiteSpace(options.Style)) payload["style"] = options.Style;
        return await ToNavyOpenAIImagesAsync(await CreateAndPollNavyImageAsync(payload, cancellationToken),
            options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size
            };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        var references = new List<string>();
        foreach (var reference in options.Images ?? []) if (!string.IsNullOrWhiteSpace(reference.ImageUrl)) references.Add(reference.ImageUrl);
        foreach (var file in options.ImageFiles ?? [])
        {
            await using var stream = file.OpenReadStream(); using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            references.Add($"data:{file.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}");
        }
        if (references.Count == 0) throw new ArgumentException("NavyAI image editing requires at least one image.", nameof(options));
        var payload = NavyCopyOptions(options.AdditionalProperties);
        payload["model"] = options.Model; payload["prompt"] = options.Prompt;
        payload["image_url"] = references.Count == 1 ? references[0] : references.Take(5).ToArray();
        payload["response_format"] = "b64_json"; payload["sync"] = true;
        if (!string.IsNullOrWhiteSpace(options.Size)) payload["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.Quality)) payload["quality"] = options.Quality;
        return await ToNavyOpenAIImagesAsync(await CreateAndPollNavyImageAsync(payload, cancellationToken),
            options.OutputFormat, options.Quality, options.Size, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
            if (!string.IsNullOrWhiteSpace(image.B64Json)) yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                OutputFormat = response.OutputFormat,
                Quality = response.Quality,
                Size = response.Size
            };
    }

    private async Task<NavyMediaTask> CreateAndPollNavyImageAsync(Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        { Content = new StringContent(JsonSerializer.Serialize(payload, NavyImageJson), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        NavyEnsureSuccess(response, raw, "image generation");
        using var document = JsonDocument.Parse(raw);
        var task = new NavyMediaTask(document.RootElement.Clone(), response.GetHeaders());
        if (!NavyHasMediaData(task.Root) && !string.IsNullOrWhiteSpace(NavyJobId(task.Root)))
            task = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                ct => PollNavyMediaTaskAsync(NavyJobId(task.Root)!, ct),
                value => NavyIsTerminal(value.Root), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(10), null, cancellationToken);
        if (NavyIsFailure(task.Root)) throw new InvalidOperationException($"NavyAI image generation failed: {task.Root.GetRawText()}");
        return task;
    }

    private async Task<NavyMediaTask> PollNavyMediaTaskAsync(string id, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/images/generations/{Uri.EscapeDataString(id)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        NavyEnsureSuccess(response, raw, "media job poll");
        using var document = JsonDocument.Parse(raw);
        return new NavyMediaTask(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<OpenAIImagesResponse> ToNavyOpenAIImagesAsync(NavyMediaTask task, string? outputFormat,
        string? quality, string? size, CancellationToken cancellationToken)
    {
        var images = await ResolveNavyMediaAsync(task.Root, false, cancellationToken);
        if (images.Count == 0) throw new InvalidOperationException("NavyAI image generation returned no images.");
        return new OpenAIImagesResponse
        {
            Created = new DateTimeOffset(NavyCreated(task.Root)).ToUnixTimeSeconds(),
            OutputFormat = outputFormat,
            Quality = quality,
            Size = size,
            Data = images.Select(x => new OpenAIImageData { B64Json = x.Base64, RevisedPrompt = x.RevisedPrompt }).ToList()
        };
    }

    private static string NavyImageValue(ImageFile file)
        => file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? file.Data : $"data:{file.MediaType};base64,{file.Data}";

    private sealed record NavyMediaTask(JsonElement Root, Dictionary<string, string> Headers);
    private sealed record NavyMedia(string Base64, string MediaType, string? RevisedPrompt = null);
}
