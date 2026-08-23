using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{
    private const string RenderfulVideoOperationTokenPrefix = "rfv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var warnings = GetRenderfulVideoWarnings(request);
        var payload = CreateRenderfulPayload(request.ProviderOptions, new Dictionary<string, object?>());
        payload["type"] = "text-to-video";
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetRenderfulVideoValue(payload, "resolution", request.Resolution);
        SetRenderfulVideoValue(payload, "aspect_ratio", request.AspectRatio);
        SetRenderfulVideoValue(payload, "duration", request.Duration);
        SetRenderfulVideoValue(payload, "seed", request.Seed);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Renderful video generation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = GetRenderfulString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Renderful video generation response did not include an id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeRenderfulVideoOperation(id, request.Model),
            Warnings = warnings,
            ProviderMetadata = CreateRenderfulMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var (id, model) = DecodeRenderfulVideoOperation(operation);
        ApplyAuthHeader();
        using var statusResponse = await _client.GetAsync($"v1/generations/{Uri.EscapeDataString(id)}", cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Renderful video status failed ({(int)statusResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = GetRenderfulString(root, "status");
        var metadata = CreateRenderfulMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (status is not null && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)))
        {
            return new VideoOperationErrorResult
            {
                Error = GetRenderfulString(root, "error", "message")
                    ?? GetRenderfulString(root, "message")
                    ?? $"Renderful video generation '{id}' failed with status '{status}'.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var outputs = GetRenderfulOutputUrls(root);
        if (outputs.Count == 0)
            return new VideoOperationErrorResult { Error = $"Renderful video generation '{id}' completed without output videos.", ProviderMetadata = metadata, Response = response };

        List<VideoOperationVideoData> videos = [];
        foreach (var output in outputs)
        {
            var (bytes, mediaType) = await DownloadOutputAsync(output, GuessRenderfulVideoMediaType(output), cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(bytes), MediaType = mediaType });
        }

        return new VideoOperationCompletedResult { Videos = videos, Warnings = [], ProviderMetadata = metadata, Response = response };
    }

    private static void SetRenderfulVideoValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null)
            payload[name] = value;
    }

    private static List<object> GetRenderfulVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "mediaInputs", details = "Supply model-specific media fields through providerOptions.renderful." });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null) warnings.Add(new { type = "unsupported", feature = "n" });
        return warnings;
    }

    private static string GuessRenderfulVideoMediaType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";

    private static string EncodeRenderfulVideoOperation(string id, string model)
        => EncodeRenderfulVideoToken(new Dictionary<string, string> { ["id"] = id, ["model"] = model });

    private static string EncodeRenderfulVideoToken(Dictionary<string, string> data)
        => RenderfulVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Id, string Model) DecodeRenderfulVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(RenderfulVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware Renderful video operation token is required.", nameof(operation));
        try
        {
            var value = operation[RenderfulVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            var id = GetRenderfulString(document.RootElement, "id");
            var model = GetRenderfulString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The Renderful video operation token is invalid.", nameof(operation));
            return (id, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Renderful video operation token is invalid.", nameof(operation), exception);
        }
    }
}
