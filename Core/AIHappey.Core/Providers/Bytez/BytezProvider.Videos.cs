using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Bytez;

public partial class BytezProvider
{
    private static readonly JsonSerializerOptions BytezVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.ProviderOptions is not null && request.ProviderOptions.Count > 0)
            warnings.Add(new { type = "unsupported", feature = "providerOptions" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Prompt
        };

        using var createReq = new HttpRequestMessage(HttpMethod.Post, request.Model)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, BytezVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bytez video request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var doc = JsonDocument.Parse(createRaw);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Bytez video request failed: {errorEl.GetRawText()}");

        var outputUrl = root.TryGetProperty("output", out var outputEl) ? outputEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(outputUrl))
            throw new InvalidOperationException("Bytez video response missing 'output' URL.");

        using var fileResp = await _client.GetAsync(outputUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Bytez video download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessVideoMediaType(outputUrl)
            ?? "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(fileBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".mp4")) return "video/mp4";
        if (u.Contains(".webm")) return "video/webm";
        if (u.Contains(".mov")) return "video/quicktime";
        if (u.Contains(".mkv")) return "video/x-matroska";
        if (u.Contains(".avi")) return "video/x-msvideo";

        return null;
    }

    private async Task<CreateMessageResult> VideoSamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var input = string.Join("\n\n", chatRequest
            .Messages
            .Where(a => a.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(z => z.Content.OfType<TextContentBlock>())
            .Select(a => a.Text));

        if (string.IsNullOrWhiteSpace(input))
            throw new Exception("No prompt provided.");

        var model = chatRequest.GetModel();
        if (string.IsNullOrWhiteSpace(model))
            throw new Exception("No model provided.");

        var videoRequest = new VideoRequest
        {
            Model = model,
            Prompt = input,
            ProviderOptions = chatRequest.Metadata.ToDictionary()
        };

        var result = await this.VideoRequest(videoRequest, cancellationToken) ?? throw new Exception("No result.");
        var firstVideo = result.Videos?.FirstOrDefault() ?? throw new Exception("No video generated.");

        return new CreateMessageResult
        {
            Model = result.Response.ModelId,
            StopReason = "unknown",
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new BlobResourceContents
                    {
                        Uri = "bytez://video/output",
                        MimeType = firstVideo.MediaType,
                        Blob = firstVideo.Data
                    }
                }
            ]
        };
    }
}

