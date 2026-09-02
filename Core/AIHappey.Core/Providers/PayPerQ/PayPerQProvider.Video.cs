using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.PayPerQ;

public partial class PayPerQProvider
{
    private const string PayPerQVideoOperationPrefix = "ppqv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var payload = CopyPayPerQOptions(request.ProviderOptions);
        payload["model"] = request.Model; payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["quality"] = request.Resolution;
        if (request.Duration is not null) payload["duration"] = request.Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reference = request.Image ?? request.InputReferences?.FirstOrDefault();
        if (reference is not null) payload["image_url"] = PayPerQVideoImage(reference);

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsurePayPerQSuccess(response, raw, "video submission");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = PayPerQGetString(root, "id") ?? throw new InvalidOperationException("PayPerQ video submission returned no id.");
        var statusUrl = PayPerQGetString(root, "status_url");
        var warnings = new List<object>();
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        if (request.InputReferences?.Skip(reference is null ? 0 : 1).Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "multiple input references" });
        return new VideoOperationStartResult
        {
            Operation = EncodePayPerQVideoOperation(id, request.Model, statusUrl), Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(PayPerQCreated(root)).UtcDateTime,
                Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var data = DecodePayPerQVideoOperation(operation);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            !string.IsNullOrWhiteSpace(data.StatusUrl) ? data.StatusUrl : $"v1/videos/{Uri.EscapeDataString(data.Id)}");
        if (string.IsNullOrWhiteSpace(data.StatusUrl)) ApplyAuthHeader();
        else request.Headers.Authorization = null;
        using var statusResponse = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsurePayPerQSuccess(statusResponse, raw, "video status");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var response = new HeaderResponseData
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(PayPerQCreated(root)).UtcDateTime,
            Headers = statusResponse.GetHeaders(), ModelId = data.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var status = PayPerQGetString(root, "status")?.Trim().ToLowerInvariant();
        if (status is "failed" or "error" or "cancelled" or "canceled")
            return new VideoOperationErrorResult
            {
                Error = ReadPayPerQVideoError(root) ?? $"PayPerQ video job '{data.Id}' failed.",
                ProviderMetadata = metadata, Response = response
            };
        if (status is not "completed" and not "complete" and not "succeeded" and not "success" and not "done")
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };
        var url = FindPayPerQVideoUrl(root);
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult
            {
                Error = $"PayPerQ video job '{data.Id}' completed without a video URL.",
                ProviderMetadata = metadata, Response = response
            };
        var media = await DownloadPayPerQMediaAsync(url, true, cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", MediaType = media.MediaType, Data = media.Base64 }],
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static string? FindPayPerQVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object) return PayPerQGetString(data, "url", "video_url", "output_url");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                return PayPerQGetString(data[0], "url", "video_url", "output_url");
        }
        return PayPerQGetString(root, "url", "video_url", "output_url");
    }

    private static string? ReadPayPerQVideoError(JsonElement root)
    {
        var direct = PayPerQGetString(root, "error", "message");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            return PayPerQGetString(error, "message", "detail", "code") ?? error.GetRawText();
        return null;
    }

    private static string EncodePayPerQVideoOperation(string id, string model, string? statusUrl)
    {
        var json = JsonSerializer.Serialize(new PayPerQVideoOperation(id, model, statusUrl));
        return PayPerQVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static PayPerQVideoOperation DecodePayPerQVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(PayPerQVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A valid model-aware PayPerQ video operation token is required.", nameof(operation));
        try
        {
            var value = operation[PayPerQVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<PayPerQVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            if (result is null || string.IsNullOrWhiteSpace(result.Id) || string.IsNullOrWhiteSpace(result.Model)) throw new JsonException();
            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new ArgumentException("The PayPerQ video operation token is invalid.", nameof(operation), exception); }
    }

    private static string PayPerQVideoImage(VideoFile image)
        => image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? image.Data : $"data:{image.MediaType};base64,{image.Data}";

    private sealed record PayPerQVideoOperation(string Id, string Model, string? StatusUrl);
}
