using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CheaperInference;

public partial class CheaperInferenceProvider
{
    private const string CheaperInferenceVideoOperationPrefix = "civ1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = ReadCheaperInferenceOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetCheaperInferenceValue(payload, "n", request.N);
        SetCheaperInferenceValue(payload, "duration", request.Duration);
        SetCheaperInferenceValue(payload, "resolution", request.Resolution);
        SetCheaperInferenceValue(payload, "aspect_ratio", request.AspectRatio);
        SetCheaperInferenceValue(payload, "seed", request.Seed);
        SetCheaperInferenceValue(payload, "fps", request.Fps);
        SetCheaperInferenceValue(payload, "generate_audio", request.GenerateAudio);

        var warnings = new List<object>();
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "input references", details = "Cheaper Inference currently documents text-to-video generation only." });

        var result = await SendCheaperInferenceJsonAsync(HttpMethod.Post, "v1/videos/generations", payload, "video generation", cancellationToken);
        var outputs = ExtractCheaperInferenceVideoOutputs(result.Root);
        if (outputs.Count == 0) throw new InvalidOperationException("Cheaper Inference video response did not contain a videos array with usable output.");
        var createdAt = ReadCheaperInferenceTimestamp(result.Root);
        var requestId = ReadCheaperInferenceString(result.Root, "id", "request_id")
            ?? ReadCheaperInferenceLedgerRequestId(result.Root)
            ?? Guid.NewGuid().ToString("N");
        var operationData = new CheaperInferenceVideoOperation(
            requestId, request.Model, createdAt, result.Headers, result.Root.GetRawText(), outputs);

        return new VideoOperationStartResult
        {
            Operation = EncodeCheaperInferenceVideoOperation(operationData),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = createdAt,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var data = DecodeCheaperInferenceVideoOperation(operation);
        using var metadataDocument = JsonDocument.Parse(data.RawResponse);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(metadataDocument.RootElement.Clone());
        var response = new HeaderResponseData
        {
            Timestamp = data.CreatedAt,
            Headers = data.Headers,
            // The submitted model carried in the opaque token is authoritative.
            ModelId = data.Model.ToModelId(GetIdentifier())
        };
        try
        {
            var videos = new List<VideoOperationVideoData>(data.Outputs.Count);
            foreach (var output in data.Outputs)
                videos.Add(await ResolveCheaperInferenceVideoOutputAsync(output, cancellationToken));
            return new VideoOperationCompletedResult
            {
                Videos = videos, Warnings = [], ProviderMetadata = metadata, Response = response
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new VideoOperationErrorResult
            {
                Error = exception.Message, ProviderMetadata = metadata, Response = response
            };
        }
    }

    private async Task<VideoOperationVideoData> ResolveCheaperInferenceVideoOutputAsync(
        CheaperInferenceVideoOutput output, CancellationToken cancellationToken)
    {
        if (!output.IsUrl)
            return new VideoOperationVideoData { Type = "base64", Data = output.Value, MediaType = output.MediaType };
        using var response = await _downloadClient.GetAsync(output.Value, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"Cheaper Inference video download failed ({(int)response.StatusCode}).");
        return new VideoOperationVideoData
        {
            Type = "base64", Data = Convert.ToBase64String(bytes),
            MediaType = response.Content.Headers.ContentType?.MediaType ?? GuessCheaperInferenceVideoMediaType(output.Value)
        };
    }

    private static List<CheaperInferenceVideoOutput> ExtractCheaperInferenceVideoOutputs(JsonElement root)
    {
        var outputs = new List<CheaperInferenceVideoOutput>();
        if (!root.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array) return outputs;
        foreach (var item in videos.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddCheaperInferenceVideoOutput(outputs, item.GetString(), null);
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var mediaType = ReadCheaperInferenceString(item, "media_type", "mime_type");
            var base64 = ReadCheaperInferenceString(item, "b64_json", "base64", "base64_data", "data");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                outputs.Add(new CheaperInferenceVideoOutput(
                    RemoveCheaperInferenceDataUrlPrefix(base64),
                    ReadCheaperInferenceDataUrlMediaType(base64) ?? mediaType ?? "video/mp4", false));
                continue;
            }
            AddCheaperInferenceVideoOutput(outputs, ReadCheaperInferenceString(item, "url", "video_url"), mediaType);
        }
        return outputs;
    }

    private static void AddCheaperInferenceVideoOutput(List<CheaperInferenceVideoOutput> outputs, string? value, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (IsCheaperInferenceHttpUrl(value))
            outputs.Add(new CheaperInferenceVideoOutput(value, mediaType ?? GuessCheaperInferenceVideoMediaType(value), true));
        else
            outputs.Add(new CheaperInferenceVideoOutput(RemoveCheaperInferenceDataUrlPrefix(value),
                ReadCheaperInferenceDataUrlMediaType(value) ?? mediaType ?? "video/mp4", false));
    }

    private static string? ReadCheaperInferenceLedgerRequestId(JsonElement root)
        => root.TryGetProperty("cheaper_inference", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? ReadCheaperInferenceString(metadata, "request_id") : null;

    private static string GuessCheaperInferenceVideoMediaType(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && Path.GetExtension(uri.AbsolutePath).Equals(".webm", StringComparison.OrdinalIgnoreCase)
                ? "video/webm" : "video/mp4";

    private static string EncodeCheaperInferenceVideoOperation(CheaperInferenceVideoOperation operation)
        => CheaperInferenceVideoOperationPrefix + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(operation, CheaperInferenceMediaJson)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CheaperInferenceVideoOperation DecodeCheaperInferenceVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(CheaperInferenceVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware Cheaper Inference video operation token is required.", nameof(operation));
        try
        {
            var encoded = operation[CheaperInferenceVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var data = JsonSerializer.Deserialize<CheaperInferenceVideoOperation>(
                Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), CheaperInferenceMediaJson);
            if (data is null || string.IsNullOrWhiteSpace(data.RequestId) || string.IsNullOrWhiteSpace(data.Model)
                || data.CreatedAt == default || string.IsNullOrWhiteSpace(data.RawResponse) || data.Headers is null
                || data.Outputs is null || data.Outputs.Count == 0
                || data.Outputs.Any(static output => string.IsNullOrWhiteSpace(output.Value) || string.IsNullOrWhiteSpace(output.MediaType)))
                throw new JsonException();
            using var _ = JsonDocument.Parse(data.RawResponse);
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Cheaper Inference video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record CheaperInferenceVideoOperation(
        string RequestId, string Model, DateTime CreatedAt, Dictionary<string, string> Headers,
        string RawResponse, IReadOnlyList<CheaperInferenceVideoOutput> Outputs);

    private sealed record CheaperInferenceVideoOutput(string Value, string MediaType, bool IsUrl);
}
