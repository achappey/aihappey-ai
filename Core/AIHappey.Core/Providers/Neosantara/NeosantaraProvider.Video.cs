using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Neosantara;

public partial class NeosantaraProvider
{
    private const string NeosantaraVideoTokenPrefix = "nsv1_";
    private static readonly JsonSerializerOptions NeosantaraVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HashSet<string> NeosantaraVideoReserved =
        new(["model", "prompt", "input_reference", "seconds", "size"], StringComparer.OrdinalIgnoreCase);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyNeosantaraJsonObject(metadata, NeosantaraVideoReserved);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["seconds"] = (request.Duration ?? 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
        payload["size"] = request.Resolution ?? "1280x720";
        var reference = ResolveNeosantaraVideoReference(request);
        if (!string.IsNullOrWhiteSpace(reference))
            payload["input_reference"] = reference;

        var warnings = new List<object>();
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, NeosantaraVideoJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Neosantara video creation failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = ReadNeosantaraString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Neosantara video creation returned no id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeNeosantaraVideoOperation(id, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadNeosantaraUnixTime(root, "created_at") ?? DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));
        var operationData = DecodeNeosantaraVideoOperation(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/videos/{Uri.EscapeDataString(operationData.Id)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Neosantara video status failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadNeosantaraString(root, "status")?.Trim().ToLowerInvariant();
        var model = !string.IsNullOrWhiteSpace(operationData.Model)
            ? operationData.Model
            : ReadNeosantaraString(root, "model") ?? GetIdentifier();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var resultResponse = new HeaderResponseData
        {
            Timestamp = ReadNeosantaraUnixTime(root, "completed_at") ?? ReadNeosantaraUnixTime(root, "created_at") ?? DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (status is "failed" or "cancelled" or "canceled")
            return new VideoOperationErrorResult
            {
                Error = ReadNeosantaraNestedString(root, "error", "message") ?? "Neosantara video generation failed.",
                ProviderMetadata = metadata,
                Response = resultResponse
            };
        if (status is not "completed")
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = resultResponse };

        using var contentResponse = await _client.GetAsync($"v1/videos/{Uri.EscapeDataString(operationData.Id)}/content", cancellationToken);
        var bytes = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResponse.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"Neosantara video content download failed ({(int)contentResponse.StatusCode}).");
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                Data = Convert.ToBase64String(bytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = resultResponse
        };
    }

    private static string EncodeNeosantaraVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new NeosantaraVideoOperationData(id, model), NeosantaraVideoJson);
        return NeosantaraVideoTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static NeosantaraVideoOperationData DecodeNeosantaraVideoOperation(string operation)
    {
        if (!operation.StartsWith(NeosantaraVideoTokenPrefix, StringComparison.Ordinal))
            return new(Uri.UnescapeDataString(operation), null);
        try
        {
            var base64 = operation[NeosantaraVideoTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            if (base64.Length % 4 != 0) base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');
            var value = JsonSerializer.Deserialize<NeosantaraVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), NeosantaraVideoJson);
            if (value is null || string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Model))
                throw new ArgumentException("The Neosantara video operation token is invalid.", nameof(operation));
            return value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Neosantara video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ResolveNeosantaraVideoReference(VideoRequest request)
    {
        var file = request.Image ?? request.InputReferences?.FirstOrDefault() ?? request.FrameImages?.FirstOrDefault()?.Image;
        if (file is null || string.IsNullOrWhiteSpace(file.Data))
            return null;
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return file.Data.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
    }

    private static string? ReadNeosantaraNestedString(JsonElement root, string parent, string child)
        => root.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object ? ReadNeosantaraString(value, child) : null;

    private sealed record NeosantaraVideoOperationData(string Id, string? Model);
}
