using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    private async Task<ImageResponse> ImageRequestQwenImageEdit(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Qwen-Image Edit requires one input image.", nameof(request));

        var warnings = new List<object>();
        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Only files[0] was used." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "The endpoint returns one image per task." });

        var image = files[0].Data;
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            image = image.RemoveDataUrlPrefix();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var outputFormat = metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("output_format", out var outputFormatElement)
            && outputFormatElement.ValueKind == JsonValueKind.String
                ? outputFormatElement.GetString()
                : "jpeg";

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["image"] = image,
            ["seed"] = request.Seed ?? -1,
            ["output_format"] = outputFormat
        };

        using var submitRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://api.novita.ai/v3/async/qwen-image-edit"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Novita Qwen-Image Edit submission failed ({submitResponse.StatusCode}): {submitRaw}");

        var taskId = ReadTaskId(submitRaw);
        var taskResultJson = await PollTaskResultJson(taskId, cancellationToken);
        var (status, reason, imageUrls) = ReadImageUrls(taskResultJson);
        if (!string.Equals(status, "TASK_STATUS_SUCCEED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Novita Qwen-Image Edit task failed (status={status}): {reason}");
        if (imageUrls.Count == 0)
            throw new InvalidOperationException("Novita Qwen-Image Edit returned no images.");

        var images = new List<string>();
        foreach (var url in imageUrls)
            images.Add(await DownloadAsDataUrlAsync(url, cancellationToken));

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { taskId }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public static bool IsQwenImageEditModel(string? model)
        => string.Equals(model, "qwen-image-edit", StringComparison.OrdinalIgnoreCase);
}
