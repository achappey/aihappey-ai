using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.ImageRouter;

public partial class ImageRouterProvider
{
    private const string ImageRouterVideoOperationTokenPrefix = "irv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var warnings = BuildVideoWarnings(request);
        var startedAt = DateTime.UtcNow;
        using var httpRequest = CreateVideoRequestMessage(request, warnings);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ImageRouter video generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        ThrowIfImageRouterError(root, "video generation");

        var pollUrl = GetPollUrl(root);
        if (string.IsNullOrWhiteSpace(pollUrl))
            throw new InvalidOperationException("ImageRouter video generation returned no fetch_result or result_url for polling.");

        return new VideoOperationStartResult
        {
            Operation = EncodeImageRouterVideoOperation(pollUrl, request.Model),
            Warnings = warnings,
            ProviderMetadata = BuildProviderMetadata(root),
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var (pollUrl, model) = DecodeImageRouterVideoOperation(operation);
        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, pollUrl);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ImageRouter video status failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var metadata = BuildProviderMetadata(root);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (TryGetStatus(root, out var status)
            && (status is "failed" or "error" or "cancelled" or "canceled"))
        {
            return new VideoOperationErrorResult
            {
                Error = $"ImageRouter video generation failed: {GetErrorMessage(root)}",
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        if (!IsImageRouterTerminal(root))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = responseData };

        var outputs = await ExtractVideoOutputsAsync(root, cancellationToken);
        if (outputs.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = "ImageRouter video generation completed but returned no usable video output.",
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = outputs.Select(video => new VideoOperationVideoData
            {
                Type = "base64",
                Data = video.Data,
                MediaType = video.MediaType
            }),
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static string EncodeImageRouterVideoOperation(string pollUrl, string model)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["pollUrl"] = pollUrl,
            ["model"] = model
        }, ImageRouterJsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return ImageRouterVideoOperationTokenPrefix + encoded;
    }

    private static (string PollUrl, string Model) DecodeImageRouterVideoOperation(string operation)
    {
        if (!operation.StartsWith(ImageRouterVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The ImageRouter video operation token is invalid.", nameof(operation));

        try
        {
            var encoded = operation[ImageRouterVideoOperationTokenPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            var remainder = encoded.Length % 4;
            if (remainder != 0)
                encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            var pollUrl = root.TryGetProperty("pollUrl", out var pollUrlElement) ? pollUrlElement.GetString() : null;
            var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(pollUrl) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The ImageRouter video operation token is invalid.", nameof(operation));

            return (pollUrl, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The ImageRouter video operation token is invalid.", nameof(operation), exception);
        }
    }

    private HttpRequestMessage CreateVideoRequestMessage(VideoRequest request, List<object> warnings)
    {
        var payload = BuildVideoPayload(request, warnings);

        if (request.Image is null)
        {
            var body = JsonSerializer.Serialize(payload, ImageRouterJsonOptions);
            return new HttpRequestMessage(HttpMethod.Post, "v1/openai/videos/generations")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        var multipart = new MultipartFormDataContent();

        foreach (var entry in payload)
            AddMultipartValue(multipart, entry.Key, entry.Value);

        multipart.Add(CreateFileContent(request.Image), "image[]", GetFileName(request.Image.MediaType, "image"));

        return new HttpRequestMessage(HttpMethod.Post, "v1/openai/videos/generations")
        {
            Content = multipart
        };
    }

    private Dictionary<string, object?> BuildVideoPayload(VideoRequest request, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["response_format"] = ResolveBase64ResponseFormat(request.ProviderOptions)
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        var size = ResolveVideoSize(request, warnings);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (request.Duration.HasValue)
            payload["seconds"] = request.Duration.Value;

        MergeRawProviderOptions(payload, request.ProviderOptions);

        payload["model"] = request.Model;
        payload["response_format"] = ResolveBase64ResponseFormat(request.ProviderOptions);

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (request.Duration.HasValue)
            payload["seconds"] = request.Duration.Value;

        return payload;
    }

    private List<object> BuildVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", property = "seed" });

        if (request.N.HasValue)
            warnings.Add(new { type = "unsupported", property = "n" });

        if (request.Fps.HasValue)
            warnings.Add(new { type = "unsupported", property = "fps" });

        return warnings;
    }

    private static string? ResolveVideoSize(VideoRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            return request.Resolution;

        if (TryResolveAspectRatioSize(request.AspectRatio, 1280, 1280, out var inferred))
        {
            warnings.Add(new
            {
                type = "mapped_property",
                property = "aspectRatio",
                mappedTo = "size",
                value = inferred
            });

            return inferred;
        }

        return null;
    }
}
