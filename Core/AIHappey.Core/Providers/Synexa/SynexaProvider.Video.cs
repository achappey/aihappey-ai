using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    private const string SynexaVideoOperationPrefix = "sxv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio,
            ["fps"] = request.Fps,
            ["seed"] = request.Seed,
            ["generate_audio"] = request.GenerateAudio
        };
        if (request.Image is not null)
            input["image"] = $"data:{request.Image.MediaType};base64,{request.Image.Data}";

        var firstFrame = request.FrameImages?.FirstOrDefault(frame => string.Equals(frame.FrameType, "first_frame", StringComparison.OrdinalIgnoreCase));
        var lastFrame = request.FrameImages?.FirstOrDefault(frame => string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase));
        if (firstFrame is not null)
            input["first_frame_image"] = $"data:{firstFrame.Image.MediaType};base64,{firstFrame.Image.Data}";
        if (lastFrame is not null)
            input["last_frame_image"] = $"data:{lastFrame.Image.MediaType};base64,{lastFrame.Image.Data}";

        MergeSynexaInputMetadata(input, metadata,
            "prompt", "duration", "resolution", "aspect_ratio", "fps", "seed", "generate_audio", "image", "first_frame_image", "last_frame_image");

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var model = request.Model;
        return new VideoOperationStartResult
        {
            Operation = EncodeSynexaVideoOperation(prediction.Id, model),
            Warnings = request.N is > 1 ? [new { type = "unsupported", feature = "n" }] : [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(CreateSynexaPredictionMetadata(prediction)),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeSynexaVideoOperation(operation);
        var prediction = await GetPredictionAsync(operationData.PredictionId, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(CreateSynexaPredictionMetadata(prediction));
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.Equals(prediction.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(prediction.Status, "canceled", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult
            {
                Error = prediction.Error ?? $"Synexa prediction ended with status '{prediction.Status}'.",
                ProviderMetadata = metadata,
                Response = response
            };

        if (!string.Equals(prediction.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var outputs = ExtractStringOutputs(prediction.Output).ToList();
        if (outputs.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = "Synexa video prediction succeeded but returned no output.",
                ProviderMetadata = metadata,
                Response = response
            };

        var videos = new List<VideoOperationVideoData>(outputs.Count);
        foreach (var output in outputs)
        {
            var (bytes, mimeType) = await ResolveOutputBytesAsync(output, "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(bytes),
                MediaType = mimeType
            });
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeSynexaVideoOperation(string predictionId, string model)
    {
        var json = JsonSerializer.Serialize(new SynexaVideoOperationData(predictionId, model), SynexaJson);
        return SynexaVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static SynexaVideoOperationData DecodeSynexaVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(SynexaVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Synexa video operation token is invalid.", nameof(operation));

        try
        {
            var base64 = operation[SynexaVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<SynexaVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), SynexaJson);
            if (result is null || string.IsNullOrWhiteSpace(result.PredictionId) || string.IsNullOrWhiteSpace(result.Model))
                throw new FormatException();
            return result;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The Synexa video operation token is invalid.", nameof(operation), ex);
        }
    }

    private sealed record SynexaVideoOperationData(string PredictionId, string Model);
}
