using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OPEAI;

public partial class OPEAIProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = new JsonObject();
        if (request.N.HasValue) payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = request.Size;
        ApplyOPEAIRawOptions(payload, GetOPEAIProviderOptions(request.ProviderOptions), "model", "prompt");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        var result = await SendOPEAIJsonAsync("v1/images/generations", payload, "image generation", cancellationToken);
        var images = ReadOPEAIImages(result.Root);
        var usage = ReadOPEAIImageUsage(result.Root);

        return new ImageResponse
        {
            Images = images.Select(static image => $"data:image/png;base64,{image}"),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Usage = usage is null ? null : new ImageUsageData
            {
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens
            },
            Response = new HeaderResponseData
            {
                Timestamp = ReadOPEAICreated(result.Root),
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }


    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var payload = new JsonObject();
        if (options.N.HasValue) payload["n"] = options.N.Value;
        if (!string.IsNullOrWhiteSpace(options.Size)) payload["size"] = options.Size;
        ApplyOPEAIRawOptions(payload, options.AdditionalProperties, "model", "prompt");
        payload["model"] = options.Model;
        payload["prompt"] = options.Prompt;

        var result = await SendOPEAIJsonAsync("v1/images/generations", payload, "image generation", cancellationToken);
        var usage = ReadOPEAIImageUsage(result.Root);
        return new OpenAIImagesResponse
        {
            Created = new DateTimeOffset(ReadOPEAICreated(result.Root)).ToUnixTimeSeconds(),
            Background = options.Background,
            OutputFormat = options.OutputFormat,
            Quality = options.Quality,
            Size = options.Size,
            Data = ReadOPEAIImages(result.Root).Select(static image => new OpenAIImageData { B64Json = image }).ToList(),
            Usage = usage
        };
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
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
                    Background = response.Background,
                    OutputFormat = response.OutputFormat,
                    Quality = response.Quality,
                    Size = response.Size,
                    Usage = response.Usage
                };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OPE AI does not document an image edit endpoint.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OPE AI does not document an image edit endpoint.");

    private static void ApplyOPEAIRawOptions(JsonObject payload, JsonElement? rawOptions, params string[] protectedNames)
    {
        var protectedSet = new HashSet<string>(protectedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in CreateOPEAIPayload(rawOptions))
            if (!protectedSet.Contains(property.Key)) payload[property.Key] = property.Value?.DeepClone();
    }

    private static void ApplyOPEAIRawOptions(JsonObject payload, Dictionary<string, JsonElement>? rawOptions, params string[] protectedNames)
    {
        var protectedSet = new HashSet<string>(protectedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var property in CreateOPEAIPayload(rawOptions))
            if (!protectedSet.Contains(property.Key)) payload[property.Key] = property.Value?.DeepClone();
    }

    private static List<string> ReadOPEAIImages(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OPE AI image response did not contain a data array.");

        var images = data.EnumerateArray()
            .Select(static item => item.TryGetProperty("b64_json", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .ToList();

        if (images.Count == 0)
            throw new InvalidOperationException("OPE AI image response did not contain any base64 images.");
        return images;
    }

    private static OpenAIImageUsage? ReadOPEAIImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        return new OpenAIImageUsage
        {
            OutputTokens = ReadOPEAIInt(usage, "output_tokens"),
            TotalTokens = ReadOPEAIInt(usage, "total_tokens")
        };
    }

    private static DateTime ReadOPEAICreated(JsonElement root)
        => root.TryGetProperty("created", out var created) && created.TryGetInt64(out var unixTime)
            ? DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime
            : DateTime.UtcNow;

}
