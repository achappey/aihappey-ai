using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Synexa;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<VideoResponse> VideoRequest(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var metadata = request.GetProviderMetadata<SynexaVideoProviderMetadata>(GetIdentifier());

        var input = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio
        };

        if (request.Seed is not null)
            input["seed"] = request.Seed;

        if (request.Image is not null)
            input["image"] = $"data:{request.Image.MediaType};base64,{request.Image.Data}";

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var completed = await WaitPredictionAsync(prediction, metadata?.Wait, cancellationToken);

        var outputValue = ExtractStringOutputs(completed.Output).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(outputValue))
            throw new InvalidOperationException("Synexa video prediction returned no outputs.");

        var videoBytes = await ResolveVideoBytesAsync(outputValue!, cancellationToken);
        var mediaType = ResolveVideoMediaType(outputValue!, request);

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    predictionId = completed.Id,
                    status = completed.Status,
                    output = completed.Output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                        ? default(object)
                        : completed.Output.Clone()
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed.Output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : completed.Output.Clone()
            }
        };
    }

    private async Task<byte[]> ResolveVideoBytesAsync(string outputValue, CancellationToken cancellationToken)
    {
        if (outputValue.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = outputValue.IndexOf(',');
            if (comma > 0)
                return Convert.FromBase64String(outputValue[(comma + 1)..]);
        }

        using var resp = await _client.GetAsync(outputValue, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Failed to download Synexa video output ({(int)resp.StatusCode}).");

        return bytes;
    }

    private static string ResolveVideoMediaType(string outputValue, VideoRequest request)
    {
        if (outputValue.StartsWith("data:video/", StringComparison.OrdinalIgnoreCase))
        {
            var semi = outputValue.IndexOf(';');
            if (semi > 5)
                return outputValue[5..semi];
        }

        if (outputValue.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (outputValue.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            return "video/mp4";

        return "video/mp4";
    }
}

