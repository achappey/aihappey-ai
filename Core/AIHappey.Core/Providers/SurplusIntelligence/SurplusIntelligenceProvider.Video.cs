using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SurplusIntelligence;

public sealed partial class SurplusIntelligenceProvider
{
    private const string VideoOperationPrefix = "siv1_";
    private static readonly JsonSerializerOptions VideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record VideoOperationData(string Id, string Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var warnings = new List<object>();
        if (request.AspectRatio is not null)
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "One video is created per operation." });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (request.InputReferences?.Skip(1).Any() == true)
            warnings.Add(new { type = "unsupported", feature = "multiple inputReferences" });
        if (request.GenerateAudio is not null)
            warnings.Add(new { type = "unsupported", feature = "generateAudio" });

        var payload = CopyProviderOptions(request.ProviderOptions);
        payload["model"] = GetNativeModel(request.Model);
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null)
            payload["seconds"] = request.Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["size"] = request.Resolution;

        var reference = request.Image ?? request.InputReferences?.FirstOrDefault();
        if (reference is not null)
            payload["input_reference"] = new Dictionary<string, object?>
            {
                ["image_url"] = ToVideoInput(reference)
            };

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(
            createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var created = await ReadVideoJsonAsync(createResponse, "video creation", cancellationToken);
        var id = ReadVideoString(created, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Surplus Intelligence video creation returned no operation id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(id, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(created),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeVideoOperation(operation);
        ApplyAuthHeader();
        using var statusResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.Id)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var status = await ReadVideoJsonAsync(statusResponse, "video status", cancellationToken);
        var state = ReadVideoString(status, "status")?.Trim().ToLowerInvariant();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(status);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (state is "failed" or "cancelled" or "canceled")
            return new VideoOperationErrorResult
            {
                Error = ReadVideoString(status, "error", "message")
                    ?? ReadVideoString(status, "error")
                    ?? $"Surplus Intelligence video operation '{operationData.Id}' failed.",
                ProviderMetadata = metadata,
                Response = response
            };
        if (state is not ("completed" or "succeeded"))
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };

        using var contentResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.Id)}/content",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var bytes = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Surplus Intelligence video download failed ({(int)contentResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}",
                null,
                contentResponse.StatusCode);

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(bytes),
                    MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4"
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static async Task<JsonElement> ReadVideoJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"Surplus Intelligence {operation} failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Surplus Intelligence {operation} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Surplus Intelligence {operation} returned invalid JSON.", exception);
        }
    }

    private static string? ReadVideoString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string ToVideoInput(VideoFile file)
    {
        if (file is VideoFileUrl url && !string.IsNullOrWhiteSpace(url.Url))
            return url.Url;
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Surplus Intelligence video input image requires data or a URL.", nameof(file));
        return file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data
            : $"data:{file.MediaType ?? "image/png"};base64,{file.Data}";
    }

    private static Dictionary<string, object?> CopyProviderOptions(
        Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerOptions is null)
            return result;
        foreach (var (name, value) in providerOptions)
            result[name] = value.Clone();
        return result;
    }

    private static string EncodeVideoOperation(string id, string model)
    {
        var data = JsonSerializer.Serialize(new VideoOperationData(id, model), VideoJsonOptions);
        return VideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(data))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static VideoOperationData DecodeVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(VideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A Surplus Intelligence video operation token is required.", nameof(operation));
        try
        {
            var value = operation[VideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<VideoOperationData>(
                Encoding.UTF8.GetString(Convert.FromBase64String(value)), VideoJsonOptions);
            return result is { Id.Length: > 0, Model.Length: > 0 }
                ? result
                : throw new JsonException("Video operation token is missing its id or model.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Surplus Intelligence video operation token is invalid.", nameof(operation), exception);
        }
    }
}
