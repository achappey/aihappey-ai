using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    private const string BlinkVideoOperationTokenPrefix = "blv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var startedAt = DateTime.UtcNow;
        var warnings = BuildVideoWarnings(request);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prompt", "model", "duration", "aspect_ratio", "image_url"
        };

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.Model
        };

        if (request.Duration is > 0)
            payload["duration"] = $"{request.Duration.Value}s";

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        // reserve canonical mapping precedence
        payload["prompt"] = request.Prompt;
        payload["model"] = request.Model;
        if (request.Duration is > 0)
            payload["duration"] = $"{request.Duration.Value}s";
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        var json = JsonSerializer.Serialize(payload, BlinkMediaJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ai/video")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Blink API error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var videoUrl = root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("url", out var urlElement)
            && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("Blink video generation returned no result URL.");

        return new VideoOperationStartResult
        {
            Operation = EncodeBlinkVideoOperation(videoUrl, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Timestamp = startedAt,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodeBlinkVideoOperation(operation);
        var downloaded = await TryFetchAsBase64Async(operationData.Url, cancellationToken);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { url = operationData.Url });

        if (downloaded is null)
            return new VideoOperationErrorResult
            {
                Error = "Unable to fetch Blink video output URL.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                Data = downloaded.Value.Base64,
                MediaType = downloaded.Value.MediaType
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static string EncodeBlinkVideoOperation(string url, string model)
    {
        var data = new Dictionary<string, string> { ["url"] = url, ["model"] = model };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, BlinkMediaJsonOptions)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return BlinkVideoOperationTokenPrefix + encoded;
    }

    private static (string Url, string Model) DecodeBlinkVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(BlinkVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Blink video operation token is invalid.", nameof(operation));

        try
        {
            var encoded = operation[BlinkVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            if (encoded.Length % 4 is var remainder && remainder != 0)
                encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The Blink video operation token is invalid.", nameof(operation));

            return (url, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Blink video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static List<object> BuildVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            AddUnsupportedWarning(warnings, "resolution", "Blink video endpoint does not accept resolution directly.");

        if (request.Seed is not null)
            AddUnsupportedWarning(warnings, "seed");

        if (request.Fps is not null)
            AddUnsupportedWarning(warnings, "fps");

        if (request.N is not null)
            AddUnsupportedWarning(warnings, "n");

        if (request.Image is not null)
            AddUnsupportedWarning(warnings, "image", "Image input in request primitives is not supported for Blink video endpoint.");

        return warnings;
    }

}

