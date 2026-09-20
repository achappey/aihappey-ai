using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.VanceAI;

public partial class VanceAIProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeVanceAIModel(request.Model);
        var source = FindVanceAIVideoSource(request)
            ?? throw new ArgumentException(
                "VanceAI video tools require a video/* input in inputReferences.",
                nameof(request));
        var config = BuildVanceAIConfig(request.ProviderOptions);
        if (request.Fps is not null && string.Equals(model, "video_upscale", StringComparison.OrdinalIgnoreCase))
            config["fps"] = JsonSerializer.SerializeToElement(request.Fps.Value);

        string sourceProperty;
        string sourceValue;
        if (IsHttpUrl(source.Data))
        {
            sourceProperty = "source_url";
            sourceValue = source.Data;
        }
        else
        {
            var uploadId = await UploadVanceAIVideoAsync(source, request.ProviderOptions, cancellationToken);
            sourceProperty = "upload_id";
            sourceValue = uploadId;
        }

        var payload = new Dictionary<string, object?>
        {
            [sourceProperty] = sourceValue,
            ["tool"] = model,
            ["config"] = config
        };
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/jobs")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VanceAIJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        createRequest.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var created = await SendVanceAIJobRequestAsync(createRequest, "create video job", cancellationToken);

        return new VideoOperationStartResult
        {
            Operation = EncodeVanceAIOperation(created.JobId, model),
            Warnings = BuildVanceAIVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(created.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = created.Headers,
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var (jobId, model) = DecodeVanceAIOperation(operation);
        var job = await GetVanceAIJobAsync(jobId, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(job.Root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = job.Headers,
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = ReadVanceAIJobError(job),
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(job.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var result = await DownloadVanceAIResultAsync(jobId, "video/mp4", cancellationToken);
        response.Headers = result.Headers;
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(result.Bytes),
                    MediaType = result.MediaType
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private async Task<string> UploadVanceAIVideoAsync(
        VideoFile source,
        Dictionary<string, JsonElement>? providerOptions,
        CancellationToken cancellationToken)
    {
        var bytes = DecodeMediaData(source.Data, out var dataUrlMediaType);
        var mediaType = dataUrlMediaType ?? source.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            mediaType = "video/mp4";
        var fileName = ReadVanceAIOptionString(providerOptions, "file_name", "fileName")
            ?? "input" + GuessExtension(mediaType);

        ApplyAuthHeader();
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "v1/uploads")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    file_name = fileName,
                    file_size = bytes.LongLength,
                    content_type = mediaType
                }, VanceAIJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var uploadResponse = await _client.SendAsync(uploadRequest, cancellationToken);
        var uploadRaw = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
            throw CreateVanceAIException("create video upload", uploadResponse.StatusCode, uploadRaw);

        using var uploadDocument = JsonDocument.Parse(uploadRaw);
        var uploadRoot = uploadDocument.RootElement;
        var uploadId = ReadVanceAIString(uploadRoot, "upload_id")
            ?? throw new InvalidOperationException("VanceAI create video upload returned no upload_id.");
        var putUrl = ReadVanceAIString(uploadRoot, "put_url")
            ?? throw new InvalidOperationException("VanceAI create video upload returned no put_url.");

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, putUrl) { Content = content };
        using var putResponse = await _transferClient.SendAsync(putRequest, cancellationToken);
        if (!putResponse.IsSuccessStatusCode)
        {
            var raw = await putResponse.Content.ReadAsStringAsync(cancellationToken);
            throw CreateVanceAIException("upload video bytes", putResponse.StatusCode, raw);
        }

        return uploadId;
    }

    private static VideoFile? FindVanceAIVideoSource(VideoRequest request)
        => (request.InputReferences ?? [])
               .FirstOrDefault(file => file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
           ?? (request.Image?.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true
               ? request.Image
               : null)
           ?? (request.FrameImages ?? [])
               .Select(frame => frame.Image)
               .FirstOrDefault(file => file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);

    private static IEnumerable<object> BuildVanceAIVideoWarnings(VideoRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt))
            yield return new { type = "unsupported", feature = "prompt" };
        if (request.N is > 1)
            yield return new { type = "unsupported", feature = "n" };
        if (request.Seed is not null)
            yield return new { type = "unsupported", feature = "seed" };
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            yield return new { type = "unsupported", feature = "resolution" };
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            yield return new { type = "unsupported", feature = "aspectRatio" };
    }
}
