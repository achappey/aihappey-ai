using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIHubMix;

public partial class AIHubMixProvider
{
    private const string AIHubMixVideoOperationTokenPrefix = "ahmv1_";
    private static readonly JsonSerializerOptions AIHubMixVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var warnings = BuildAIHubMixVideoWarnings(request);
        var payload = CopyAIHubMixProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null) payload["seconds"] = request.Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["size"] = request.Resolution;

        var reference = request.Image ?? request.InputReferences?.FirstOrDefault();
        if (reference is not null)
            payload["input_reference"] = new Dictionary<string, object?>
            {
                ["image_url"] = ToAIHubMixDataUrl(reference)
            };

        payload.Remove("duration");
        payload.Remove("resolution");
        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AIHubMixVideoJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var created = await ReadAIHubMixJsonAsync(createResponse, "video creation", cancellationToken);
        var id = GetAIHubMixString(created, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("AIHubMix video creation did not return an id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeAIHubMixVideoOperation(id, request.Model),
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

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodeAIHubMixVideoOperation(operation);
        ApplyAuthHeader();
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(operationData.Id)}");
        using var statusResponse = await _client.SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadAIHubMixJsonAsync(statusResponse, "video status", cancellationToken);
        var status = GetAIHubMixString(root, "status");
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult
            {
                Error = GetAIHubMixString(root, "error", "message") ?? $"AIHubMix video '{operationData.Id}' failed.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = responseData };

        using var contentRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(operationData.Id)}/content");
        using var contentResponse = await _client.SendAsync(contentRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var video = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIHubMix video download failed ({(int)contentResponse.StatusCode}): {Encoding.UTF8.GetString(video)}");

        var warnings = new List<object>();
        try
        {
            using var deleteResponse = await _client.DeleteAsync($"v1/videos/{Uri.EscapeDataString(operationData.Id)}", cancellationToken);
            if (!deleteResponse.IsSuccessStatusCode)
                warnings.Add(new { type = "cleanup", feature = "video deletion", statusCode = (int)deleteResponse.StatusCode });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add(new { type = "cleanup", feature = "video deletion", message = exception.Message });
        }

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(video),
                MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4"
            }],
            Warnings = warnings,
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private async Task<JsonElement> ReadAIHubMixJsonAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIHubMix {operation} failed ({(int)response.StatusCode}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"AIHubMix {operation} returned invalid JSON.", exception);
        }
    }

    private static string? GetAIHubMixString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static string EncodeAIHubMixVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new AIHubMixVideoOperationData(id, model), AIHubMixVideoJsonOptions);
        return AIHubMixVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static AIHubMixVideoOperationData DecodeAIHubMixVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(AIHubMixVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware AIHubMix video operation token is required.", nameof(operation));
        try
        {
            var encoded = operation[AIHubMixVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var data = JsonSerializer.Deserialize<AIHubMixVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), AIHubMixVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.Id) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The AIHubMix video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The AIHubMix video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static List<object> BuildAIHubMixVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        if (request.FrameImages?.Any() == true) warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (request.InputReferences?.Skip(1).Any() == true) warnings.Add(new { type = "unsupported", feature = "multiple inputReferences" });
        return warnings;
    }

    private static string ToAIHubMixDataUrl(VideoFile file)
        => file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data
            : $"data:{file.MediaType};base64,{file.Data}";

    private sealed record AIHubMixVideoOperationData(string Id, string Model);
}
