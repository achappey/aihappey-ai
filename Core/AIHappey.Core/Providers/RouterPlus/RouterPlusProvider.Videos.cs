using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RouterPlus;

public sealed partial class RouterPlusProvider
{
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
        if (request.Fps is not null || request.N is > 1 || request.FrameImages?.Any() == true
            || request.InputReferences?.Any() == true || request.Image is not null)
            throw new NotSupportedException(
                "RouterPlus video generation does not document fps, multiple outputs, frameImages, inputReferences, or image conditioning.");
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new ArgumentException("RouterPlus video generation does not document provider-specific options.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelId(request.Model),
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio,
            ["seed"] = request.Seed,
            ["generate_audio"] = request.GenerateAudio
        };
        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, VideoJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"RouterPlus video creation failed ({(int)createResponse.StatusCode}): {raw}",
                null,
                createResponse.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = ReadVideoString(root, "id")
            ?? throw new InvalidOperationException("RouterPlus video creation response contained no id.");
        var model = NormalizeModelId(request.Model);
        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(id, model),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeVideoOperation(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.Id)}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"RouterPlus video status failed ({(int)response.StatusCode}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadVideoString(root, "status") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        if (status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = responseData };

        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult
            {
                Error = ReadVideoString(root, "error") ?? $"RouterPlus video generation ended with status '{status}'.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        using var contentResponse = await _client.GetAsync(
            $"v1/videos/{Uri.EscapeDataString(operationData.Id)}/content",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var content = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"RouterPlus video download failed ({(int)contentResponse.StatusCode}): {Encoding.UTF8.GetString(content)}",
                null,
                contentResponse.StatusCode);

        responseData.Headers = contentResponse.GetHeaders();
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(content),
                    MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4"
                }
            ],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static string EncodeVideoOperation(string id, string model)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new VideoOperationData(id, model), VideoJsonOptions))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static VideoOperationData DecodeVideoOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var value = operation.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        try
        {
            return JsonSerializer.Deserialize<VideoOperationData>(
                Convert.FromBase64String(value), VideoJsonOptions)
                is { Id.Length: > 0, Model.Length: > 0 } data
                    ? data
                    : throw new ArgumentException("RouterPlus video operation token is invalid.", nameof(operation));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("RouterPlus video operation token is invalid.", nameof(operation), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("RouterPlus video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ReadVideoString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
