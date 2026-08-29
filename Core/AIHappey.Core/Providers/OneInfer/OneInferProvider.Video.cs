using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{
    private const string OneInferVideoOperationTokenPrefix = "oiv1_";

    private sealed record OneInferVideoOperationData(
        string RequestId,
        string Model,
        DateTime CreatedAt,
        IReadOnlyList<OneInferVideoOperationOutput> Outputs);

    private sealed record OneInferVideoOperationOutput(
        string Value,
        string MediaType,
        bool IsUrl);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        await ApplyAuthHeaderAsync(cancellationToken);

        var submittedAt = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetOneInferProviderOptions(request.ProviderOptions);
        var payload = OneInferJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.Duration.HasValue)
            payload["duration"] = request.Duration.Value;
        if (request.Fps.HasValue)
            payload["fps"] = request.Fps.Value;
        if (request.Seed.HasValue)
            payload["seed"] = request.Seed.Value;
        if (request.N.HasValue)
            payload["number"] = request.N.Value;
        if (request.GenerateAudio.HasValue)
            payload["generate_audio"] = request.GenerateAudio.Value;

        var references = ResolveOneInferVideoImageReferences(request).ToList();
        if (references.Count > 0)
            payload["files"] = references;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ula/generate-video")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OneInferJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneInfer video generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var data = OneInferGetData(root);
        var outputs = ExtractOneInferVideoOperationOutputs(data);

        if (outputs.Count == 0)
            throw new InvalidOperationException("OneInfer video generation response contained no videos.");

        var requestId = OneInferTryGetString(data, "id") ?? Guid.NewGuid().ToString("N");
        var responseModel = OneInferTryGetString(data, "model") ?? request.Model;
        var createdAt = ReadOneInferUnixTimestamp(data, "created") ?? submittedAt;

        return new VideoOperationStartResult
        {
            Operation = EncodeOneInferVideoOperation(new(requestId, responseModel, createdAt, outputs)),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                id = requestId,
                model = responseModel,
                status = "ready"
            }),
            Response = new()
            {
                Timestamp = createdAt,
                Headers = response.GetHeaders(),
                ModelId = responseModel.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeOneInferVideoOperation(operation);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            id = operationData.RequestId,
            model = operationData.Model,
            status = "completed"
        });

        try
        {
            var videos = new List<VideoOperationVideoData>(operationData.Outputs.Count);
            foreach (var output in operationData.Outputs)
                videos.Add(await ResolveOneInferVideoOperationOutputAsync(output, cancellationToken));

            return new VideoOperationCompletedResult
            {
                Videos = videos,
                Warnings = [],
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VideoOperationErrorResult
            {
                Error = ex.Message,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
                {
                    id = operationData.RequestId,
                    model = operationData.Model,
                    status = "error"
                }),
                Response = response
            };
        }
    }

    private static string EncodeOneInferVideoOperation(OneInferVideoOperationData operationData)
    {
        var json = JsonSerializer.Serialize(operationData, OneInferJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return OneInferVideoOperationTokenPrefix + base64Url;
    }

    private static OneInferVideoOperationData DecodeOneInferVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(OneInferVideoOperationTokenPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The OneInfer video operation token is invalid.", nameof(operation));
        }

        var base64Url = operation[OneInferVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<OneInferVideoOperationData>(json, OneInferJsonOptions);
            if (data is null
                || string.IsNullOrWhiteSpace(data.RequestId)
                || string.IsNullOrWhiteSpace(data.Model)
                || data.CreatedAt == default
                || data.Outputs is null
                || data.Outputs.Count == 0
                || data.Outputs.Any(output => string.IsNullOrWhiteSpace(output.Value)
                    || string.IsNullOrWhiteSpace(output.MediaType)))
            {
                throw new ArgumentException("The OneInfer video operation token is invalid.", nameof(operation));
            }

            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The OneInfer video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static List<OneInferVideoOperationOutput> ExtractOneInferVideoOperationOutputs(JsonElement data)
    {
        var outputs = new List<OneInferVideoOperationOutput>();
        if (!data.TryGetProperty("videos", out var videosElement) || videosElement.ValueKind != JsonValueKind.Array)
            return outputs;

        foreach (var item in videosElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var format = OneInferTryGetString(item, "type", "format") ?? "mp4";
            var fallbackMediaType = OneInferVideoMediaTypeFromFormat(format);
            var base64 = OneInferTryGetString(item, "base64", "base64_data", "data", "b64_json");
            if (!string.IsNullOrWhiteSpace(base64))
            {
                outputs.Add(new(
                    base64.RemoveDataUrlPrefix(),
                    OneInferTryGetDataUrlMediaType(base64) ?? fallbackMediaType,
                    false));
                continue;
            }

            var url = OneInferTryGetString(item, "url", "video_url", "videoUrl");
            if (!string.IsNullOrWhiteSpace(url))
            {
                var isHttpUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                outputs.Add(new(
                    isHttpUrl ? url : url.RemoveDataUrlPrefix(),
                    OneInferTryGetDataUrlMediaType(url) ?? fallbackMediaType,
                    isHttpUrl));
            }
        }

        return outputs;
    }

    private async Task<VideoOperationVideoData> ResolveOneInferVideoOperationOutputAsync(
        OneInferVideoOperationOutput output,
        CancellationToken cancellationToken)
    {
        if (!output.IsUrl)
        {
            return new VideoOperationVideoData
            {
                Data = output.Value,
                MediaType = output.MediaType,
                Type = "base64"
            };
        }

        using var videoResponse = await _client.GetAsync(output.Value, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode || bytes.Length == 0)
        {
            throw new InvalidOperationException(
                $"Failed to download OneInfer video from returned URL ({(int)videoResponse.StatusCode}).");
        }

        return new VideoOperationVideoData
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = videoResponse.Content.Headers.ContentType?.MediaType
                ?? OneInferGuessVideoMediaType(output.Value)
                ?? output.MediaType,
            Type = "base64"
        };
    }

    private static IEnumerable<string> ResolveOneInferVideoImageReferences(VideoRequest request)
    {
        if (request.Image is not null)
            yield return NormalizeOneInferVideoFile(request.Image);

        if (request.InputReferences is not null)
        {
            foreach (var reference in request.InputReferences)
                if (reference is not null)
                    yield return NormalizeOneInferVideoFile(reference);
        }

        if (request.FrameImages is not null)
        {
            foreach (var frame in request.FrameImages)
                if (frame?.Image is not null)
                    yield return NormalizeOneInferVideoFile(frame.Image);
        }
    }

    private static string NormalizeOneInferVideoFile(VideoFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }
}
