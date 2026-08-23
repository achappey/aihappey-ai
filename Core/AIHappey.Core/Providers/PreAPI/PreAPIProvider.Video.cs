using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.PreAPI;

public partial class PreAPIProvider
{
    private const string PreApiVideoOperationTokenPrefix = "pav1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();

        var startedAt = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = $"PreAPI currently returns a single primary generation per request. Requested n={request.N}." });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var input = BuildVideoInput(request);
        using var doc = await GenerateAsync(request.Model, input, cancellationToken);

        var data = GetResponseData(doc.RootElement);
        var output = GetOutput(data);
        var videoUrl = TryGetNestedString(output, "video", "url") ?? TryGetString(data, "output_url");
        var contentType = TryGetNestedString(output, "video", "content_type");

        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("PreAPI video generation returned no video URL.");

        return new VideoOperationStartResult
        {
            Operation = EncodePreApiVideoOperation(videoUrl, request.Model, contentType),
            Warnings = warnings,
            ProviderMetadata = CreateProviderMetadata(doc.RootElement),
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodePreApiVideoOperation(operation);
        var download = await DownloadMediaAsync(operationData.Url, operationData.ContentType, cancellationToken);

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                Data = download.Base64,
                MediaType = download.MediaType
            }],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { url = operationData.Url }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = operationData.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string EncodePreApiVideoOperation(string url, string model, string? contentType)
    {
        var data = new Dictionary<string, string?>
        {
            ["url"] = url,
            ["model"] = model,
            ["contentType"] = contentType
        };
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, PreApiJsonOptions)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return PreApiVideoOperationTokenPrefix + encoded;
    }

    private static (string Url, string Model, string? ContentType) DecodePreApiVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(PreApiVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The PreAPI video operation token is invalid.", nameof(operation));

        try
        {
            var encoded = operation[PreApiVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            if (encoded.Length % 4 is var remainder && remainder != 0)
                encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
            var contentType = root.TryGetProperty("contentType", out var contentTypeElement)
                && contentTypeElement.ValueKind == JsonValueKind.String
                    ? contentTypeElement.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The PreAPI video operation token is invalid.", nameof(operation));

            return (url, model, contentType);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The PreAPI video operation token is invalid.", nameof(operation), exception);
        }
    }
}
