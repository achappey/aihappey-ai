using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMAPI;

public partial class LLMAPIProvider
{
    private const string LLMAPIVideoOperationTokenPrefix = "llmav1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = GetLLMAPIVideoOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null) payload["seconds"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["size"] = request.Resolution;
        if (request.Fps is not null) payload["fps"] = request.Fps;
        if (request.Seed is not null) payload["seed"] = request.Seed;
        if (request.GenerateAudio is not null) payload["generate_audio"] = request.GenerateAudio;

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.N is not null) warnings.Add(new { type = "unsupported", feature = "n" });

        var images = new List<string>();
        if (request.Image is not null) payload["image_url"] = ToLLMAPIMediaUrl(request.Image);
        if (request.InputReferences is not null) images.AddRange(request.InputReferences.Select(ToLLMAPIMediaUrl));
        if (request.FrameImages is not null)
        {
            images.AddRange(request.FrameImages
                .OrderBy(frame => string.Equals(frame.FrameType, "first_frame", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(frame => ToLLMAPIMediaUrl(frame.Image)));
        }
        if (images.Count > 0) payload["image_list"] = images;

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadLLMAPIVideoJsonAsync(response, "video submission", cancellationToken);
        var id = ReadLLMAPIString(root, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("LLMAPI video submission did not return an id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeLLMAPIVideoOperation(id, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadLLMAPIVideoTimestamp(root, "created_at") ?? DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeLLMAPIVideoOperation(operation);
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(operationData.Id)}");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadLLMAPIVideoJsonAsync(response, "video status", cancellationToken);
        var status = ReadLLMAPIString(root, "status")?.Trim().ToLowerInvariant();
        var responseData = new HeaderResponseData
        {
            Timestamp = ReadLLMAPIVideoTimestamp(root, "completed_at")
                ?? ReadLLMAPIVideoTimestamp(root, "created_at")
                ?? DateTime.UtcNow,
            Headers = response.GetHeaders(),
            // The token's submitted model is authoritative. Status payloads can omit or alter it.
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);

        if (status == "failed")
            return new VideoOperationErrorResult
            {
                Error = ReadLLMAPIString(root, "error", "message")
                    ?? ReadLLMAPIString(root, "error")
                    ?? $"LLMAPI video job '{operationData.Id}' failed.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        if (status is not "completed")
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = responseData };

        using var contentRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(operationData.Id)}/content");
        using var contentResponse = await _client.SendAsync(contentRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var video = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode)
            return new VideoOperationErrorResult
            {
                Error = $"LLMAPI video content failed ({(int)contentResponse.StatusCode}): {Encoding.UTF8.GetString(video)}",
                ProviderMetadata = metadata,
                Response = responseData
            };

        responseData.Headers = contentResponse.GetHeaders();
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(video),
                    MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4"
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private Dictionary<string, object?> GetLLMAPIVideoOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in GetLLMAPIProviderOptions(providerOptions) ?? []) result[option.Key] = option.Value.Clone();
        foreach (var reserved in new[] { "model", "prompt", "seconds", "size", "fps", "seed", "image_url", "image_list" })
            result.Remove(reserved);
        return result;
    }

    private static string ToLLMAPIMediaUrl(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Data)) throw new ArgumentException("Video image data is required.", nameof(file));
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return file.Data;
        return $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? "image/png" : file.MediaType)};base64,{file.Data}";
    }

    private static async Task<JsonElement> ReadLLMAPIVideoJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLMAPI {operation} failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string? ReadLLMAPIString(JsonElement element, string name, string nestedName)
        => element.TryGetProperty(name, out var nested) ? ReadLLMAPIString(nested, nestedName) : null;

    private static DateTime? ReadLLMAPIVideoTimestamp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    private static string EncodeLLMAPIVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new LLMAPIVideoOperation(id, model));
        return LLMAPIVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static LLMAPIVideoOperation DecodeLLMAPIVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(LLMAPIVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware LLMAPI video operation token is required.", nameof(operation));
        try
        {
            var encoded = operation[LLMAPIVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var value = JsonSerializer.Deserialize<LLMAPIVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            if (value is null || string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Model))
                throw new JsonException("Missing operation values.");
            return value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The LLMAPI video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record LLMAPIVideoOperation(string Id, string Model);
}
