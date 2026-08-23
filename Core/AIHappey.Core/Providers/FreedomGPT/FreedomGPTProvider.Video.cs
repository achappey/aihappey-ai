using AIHappey.Core.AI;
using AIHappey.Common.Model.Providers.FreedomGPT;
using AIHappey.Core.Extensions;
using System.Text.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FreedomGPT;

public partial class FreedomGPTProvider
{
    private const string FreedomGptVideoOperationTokenPrefix = "fgv1_";

    private static readonly JsonSerializerOptions FreedomGptVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var localModel = NormalizeFreedomGptVideoModel(request.Model);
        var target = await ResolveActorGptTargetAsync(localModel, request, cancellationToken);
        var warnings = BuildFreedomGptVideoWarnings(request);
        var payload = new
        {
            actorId = target.ActorId,
            voiceId = target.VoiceId,
            script = request.Prompt
        };

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/actor-gpt/create")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, FreedomGptVideoJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"FreedomGPT ActorGPT create failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var videoId = ReadFreedomGptVideoString(document.RootElement, "videoId");
        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("FreedomGPT ActorGPT create returned no videoId.");

        return new VideoOperationStartResult
        {
            Operation = EncodeFreedomGptVideoOperation(videoId, localModel),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                videoId,
                target.ActorId,
                target.VoiceId
            }),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = localModel.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeFreedomGptVideoOperation(operation);
        ApplyAuthHeader();
        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/actor-gpt/get?videoId={Uri.EscapeDataString(operationData.VideoId)}");
        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"FreedomGPT ActorGPT status failed ({(int)statusResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var videoUrl = ReadFreedomGptVideoString(root, "videoUrl");
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            videoId = operationData.VideoId,
            videoUrl
        });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = response
            };

        using var videoResponse = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode || videoBytes.Length == 0)
            throw new InvalidOperationException($"FreedomGPT ActorGPT video download failed ({(int)videoResponse.StatusCode}).");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = videoResponse.Content.Headers.ContentType?.MediaType
                    ?? GuessFreedomGptVideoMediaType(videoUrl)
                    ?? "video/mp4",
                Data = Convert.ToBase64String(videoBytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private async Task<ActorGptTarget> ResolveActorGptTargetAsync(
        string localModel,
        VideoRequest request,
        CancellationToken cancellationToken)
    {
        var metadata = GetFreedomGptVideoMetadata(request);
        string? actorId = null;
        string? voiceId = null;

        var segments = localModel.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !string.Equals(segments[0], ActorGptModelPrefix, StringComparison.OrdinalIgnoreCase) || segments.Length > 3)
            throw new ArgumentException($"Unsupported FreedomGPT video model '{request.Model}'.", nameof(request));

        if (segments.Length > 1)
        {
            var catalog = await GetActorGptCatalogAsync(cancellationToken);
            var actor = catalog.Actors.FirstOrDefault(item => string.Equals(item.Slug, segments[1], StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown FreedomGPT ActorGPT actor slug '{segments[1]}'.", nameof(request));
            actorId = actor.Id;

            if (segments.Length > 2)
            {
                var voice = catalog.Voices.FirstOrDefault(item => string.Equals(item.Slug, segments[2], StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown FreedomGPT ActorGPT voice slug '{segments[2]}'.", nameof(request));
                voiceId = voice.Id;
            }
        }

        actorId ??= metadata?.ActorId?.Trim();
        voiceId ??= metadata?.VoiceId?.Trim();

        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("FreedomGPT ActorGPT actorId is required in providerOptions.freedomgpt for the generic model.", nameof(request));
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("FreedomGPT ActorGPT voiceId is required in providerOptions.freedomgpt unless the model selects a voice.", nameof(request));

        return new ActorGptTarget(actorId, voiceId);
    }

    private static FreedomGPTVideoProviderMetadata? GetFreedomGptVideoMetadata(VideoRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue("freedomgpt", out var element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return element.Deserialize<FreedomGPTVideoProviderMetadata>(FreedomGptVideoJson);
    }

    private static List<object> BuildFreedomGptVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (request.Duration is not null) warnings.Add(new { type = "unsupported", feature = "duration" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null && request.N != 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.Resolution)) warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        if (request.Image is not null) warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.InputReferences?.Any() == true) warnings.Add(new { type = "unsupported", feature = "input_references" });
        if (request.FrameImages?.Any() == true) warnings.Add(new { type = "unsupported", feature = "frame_images" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generate_audio" });
        return warnings;
    }

    private static string NormalizeFreedomGptVideoModel(string model)
    {
        var normalized = model.Trim().Trim('/');
        const string providerPrefix = "freedomgpt/";
        return normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[providerPrefix.Length..]
            : normalized;
    }

    private static string EncodeFreedomGptVideoOperation(string videoId, string model)
    {
        var json = JsonSerializer.Serialize(new FreedomGptVideoOperationData(videoId, model), FreedomGptVideoJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return FreedomGptVideoOperationTokenPrefix + base64Url;
    }

    private static FreedomGptVideoOperationData DecodeFreedomGptVideoOperation(string operation)
    {
        if (!operation.StartsWith(FreedomGptVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware FreedomGPT video operation token is required.", nameof(operation));

        var base64Url = operation[FreedomGptVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<FreedomGptVideoOperationData>(json, FreedomGptVideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.VideoId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The FreedomGPT video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The FreedomGPT video operation token is invalid.", nameof(operation), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The FreedomGPT video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ReadFreedomGptVideoString(JsonElement item, string propertyName)
        => item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? GuessFreedomGptVideoMediaType(string url)
        => url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            ? "video/webm"
            : url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                ? "video/quicktime"
                : url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    ? "video/mp4"
                    : null;

    private sealed record ActorGptTarget(string ActorId, string VoiceId);

    private sealed record FreedomGptVideoOperationData(string VideoId, string Model);
}
