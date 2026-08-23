using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bytez;

public partial class BytezProvider
{
    private const string BytezVideoOperationTokenPrefix = "bzv1_";

    private static readonly JsonSerializerOptions BytezVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var startedAt = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        var payload = GetBytezVideoProviderOptions(request);
        payload["text"] = request.Prompt;

        using var createReq = new HttpRequestMessage(HttpMethod.Post, request.Model)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, BytezVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bytez video request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var doc = JsonDocument.Parse(createRaw);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Bytez video request failed: {errorEl.GetRawText()}");

        var outputUrl = root.TryGetProperty("output", out var outputEl) ? outputEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(outputUrl))
            throw new InvalidOperationException("Bytez video response missing 'output' URL.");

        return new VideoOperationStartResult
        {
            Operation = EncodeBytezVideoOperation(outputUrl, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodeBytezVideoOperation(operation);

        using var fileResp = await _client.GetAsync(operationData.Url, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Bytez video download failed ({(int)fileResp.StatusCode}): {error}");
        }

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = fileResp.Content.Headers.ContentType?.MediaType ?? GuessVideoMediaType(operationData.Url) ?? "video/mp4",
                Data = Convert.ToBase64String(fileBytes)
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

    private Dictionary<string, object?> GetBytezVideoProviderOptions(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in options.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static string EncodeBytezVideoOperation(string url, string model)
        => EncodeBytezVideoToken(new Dictionary<string, string> { ["url"] = url, ["model"] = model });

    private static string EncodeBytezVideoToken(Dictionary<string, string> data)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, BytezVideoJsonOptions)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return BytezVideoOperationTokenPrefix + encoded;
    }

    private static (string Url, string Model) DecodeBytezVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(BytezVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Bytez video operation token is invalid.", nameof(operation));

        try
        {
            var encoded = operation[BytezVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            if (encoded.Length % 4 is var remainder && remainder != 0)
                encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            var url = root.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The Bytez video operation token is invalid.", nameof(operation));

            return (url, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Bytez video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".mp4")) return "video/mp4";
        if (u.Contains(".webm")) return "video/webm";
        if (u.Contains(".mov")) return "video/quicktime";
        if (u.Contains(".mkv")) return "video/x-matroska";
        if (u.Contains(".avi")) return "video/x-msvideo";

        return null;
    }

   
}

