using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.AkashML;

public partial class AkashMLProvider
{
    private static readonly JsonSerializerOptions AkashMLImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = CreateAkashMLImagePayload(request.ProviderOptions, "model", "prompt", "n", "size", "response_format", "seed");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        if (request.Seed.HasValue) payload["seed"] = request.Seed.Value;

        var result = await SendAkashMLImageGenerationAsync(payload, cancellationToken);
        return new ImageResponse
        {
            Images = result.Images.Select(image => $"data:image/png;base64,{image.Base64}"),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = CreateAkashMLImagePayload(options.AdditionalProperties, "model", "prompt", "n", "size", "response_format", "user");
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;
        payload["response_format"] = "b64_json";
        if (options.N.HasValue) payload["n"] = options.N.Value;
        if (!string.IsNullOrWhiteSpace(options.Size)) payload["size"] = options.Size;
        if (!string.IsNullOrWhiteSpace(options.User)) payload["user"] = options.User;

        var result = await SendAkashMLImageGenerationAsync(payload, cancellationToken);
        return ToAkashMLOpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
        }
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageEditRequest();
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        AddAkashMLFormValue(form, "model", options.Model);
        AddAkashMLFormValue(form, "prompt", options.Prompt);
        AddAkashMLFormValue(form, "response_format", "b64_json");
        AddAkashMLFormValue(form, "n", options.N?.ToString());
        AddAkashMLFormValue(form, "size", options.Size);
        AddAkashMLFormValue(form, "user", options.User);

        foreach (var file in options.ImageFiles ?? [])
        {
            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "image", file.FileName);
        }
        foreach (var image in options.Images ?? [])
            AddAkashMLFormValue(form, "image_url", image.ImageUrl);
        if (options.MaskFile is { } mask)
        {
            var content = new StreamContent(mask.OpenReadStream());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(mask.ContentType ?? MediaTypeNames.Image.Png);
            form.Add(content, "mask", mask.FileName);
        }
        AddAkashMLFormValue(form, "mask_url", options.Mask?.ImageUrl);
        foreach (var property in options.AdditionalProperties ?? [])
            if (!AkashMLImageEditReservedFields.Contains(property.Key))
                AddAkashMLFormValue(form, property.Key, JsonElementToFormValue(property.Value));

        using var response = await _client.PostAsync("v1/images/edits", form, cancellationToken);
        var result = await ReadAkashMLImageResponseAsync(response, "edit", cancellationToken);
        return ToAkashMLOpenAIImagesResponse(result, options.Background, options.OutputFormat, options.Quality, options.Size);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageEditRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageEditCompleted { B64Json = image.B64Json, CreatedAt = response.Created, Background = response.Background, OutputFormat = response.OutputFormat, Quality = response.Quality, Size = response.Size };
        }
    }

    private static readonly HashSet<string> AkashMLImageEditReservedFields = new(StringComparer.OrdinalIgnoreCase)
    { "model", "prompt", "image", "image_url", "mask", "mask_url", "n", "size", "response_format", "user" };

    private async Task<AkashMLImageResult> SendAkashMLImageGenerationAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(payload.ToJsonString(AkashMLImageJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadAkashMLImageResponseAsync(response, "generation", cancellationToken);
    }

    private async Task<AkashMLImageResult> ReadAkashMLImageResponseAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AkashML image {operation} failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("AkashML image response did not contain a data array.");

        var images = new List<AkashMLImage>();
        foreach (var item in data.EnumerateArray())
        {
            var base64 = item.TryGetProperty("b64_json", out var b64) ? b64.GetString() : null;
            if (string.IsNullOrWhiteSpace(base64) && item.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                base64 = Convert.ToBase64String(await _client.GetByteArrayAsync(uri, cancellationToken));
            if (!string.IsNullOrWhiteSpace(base64))
                images.Add(new(base64, item.TryGetProperty("revised_prompt", out var revised) ? revised.GetString() : null));
        }
        if (images.Count == 0) throw new InvalidOperationException("AkashML image response did not contain any usable images.");
        return new(root, response.GetHeaders(), images);
    }

    private static OpenAIImagesResponse ToAkashMLOpenAIImagesResponse(AkashMLImageResult result, string? background, string? outputFormat, string? quality, string? size)
        => new()
        {
            Created = result.Root.TryGetProperty("created", out var created) && created.TryGetInt64(out var value) ? value : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Background = background, OutputFormat = outputFormat, Quality = quality, Size = size,
            Data = result.Images.Select(image => new OpenAIImageData { B64Json = image.Base64, RevisedPrompt = image.RevisedPrompt }).ToList()
        };

    private static JsonObject CreateAkashMLImagePayload(Dictionary<string, JsonElement>? properties, params string[] reserved)
    {
        var payload = new JsonObject();
        var reservedSet = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties ?? [])
            if (!reservedSet.Contains(property.Key)) payload[property.Key] = JsonNode.Parse(property.Value.GetRawText());
        return payload;
    }

    private static void AddAkashMLFormValue(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) form.Add(new StringContent(value), name);
    }

    private static string JsonElementToFormValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private sealed record AkashMLImage(string Base64, string? RevisedPrompt);
    private sealed record AkashMLImageResult(JsonElement Root, Dictionary<string, string> Headers, List<AkashMLImage> Images);
}
