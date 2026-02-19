using System.Text.Json;
using AIHappey.Common.Model.Providers.Synexa;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<ImageResponse> ImageRequest(
        ImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Files?.Skip(1).Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Only the first file is used." });

        var metadata = request.GetProviderMetadata<SynexaImageProviderMetadata>(GetIdentifier());

        var input = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["negative_prompt"] = null,
            ["num_inference_steps"] = null,
            ["guidance_scale"] = null
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
            input["size"] = request.Size;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            input["aspect_ratio"] = request.AspectRatio;

        if (request.Seed is not null)
            input["seed"] = request.Seed;

        var firstFile = request.Files?.FirstOrDefault();
        if (firstFile is not null)
            input["image"] = $"data:{firstFile.MediaType};base64,{firstFile.Data}";

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var completed = await WaitPredictionAsync(prediction, metadata?.Wait, cancellationToken);

        var outputs = ExtractStringOutputs(completed.Output).ToList();
        if (outputs.Count == 0)
            throw new InvalidOperationException("Synexa image prediction returned no outputs.");

        var images = new List<string>(outputs.Count);
        foreach (var output in outputs)
            images.Add(await DownloadImageAsDataUrlAsync(output, cancellationToken));

        return new ImageResponse
        {
            Images = images,
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
}

