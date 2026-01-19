using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    private static readonly JsonSerializerOptions Flux2ProJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestFlux2Pro(
        ImageRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Flux 2 Pro integration is text-to-image only; input images were ignored."
            });
        }

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.N is > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Flux 2 Pro returns a single image per request in this integration."
            });
        }

        var size = request.Size?.Trim().Replace("x", "*");
        if (string.IsNullOrWhiteSpace(size))
        {
            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            {
                var inferred = request.AspectRatio.InferSizeFromAspectRatio(
                    minWidth: 256,
                    maxWidth: 1536,
                    minHeight: 256,
                    maxHeight: 1536);

                if (inferred is not null)
                {
                    size = $"{inferred.Value.width}*{inferred.Value.height}";
                    warnings.Add(new
                    {
                        type = "inferred",
                        feature = "size",
                        details = $"Size inferred from aspectRatio: {size}."
                    });
                }
                else
                {
                    warnings.Add(new
                    {
                        type = "unsupported",
                        feature = "aspectRatio",
                        details = "AspectRatio could not be mapped to a valid size; default size was used."
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(size))
            {
                size = "1024*1024";
                warnings.Add(new
                {
                    type = "default",
                    feature = "size",
                    details = "No size provided; defaulted to 1024x1024."
                });
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "ignored",
                feature = "aspectRatio",
                details = "AspectRatio was ignored because explicit size was provided."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["size"] = size,
        };

        if (request.Seed is not null)
        {
            payload["seed"] = request.Seed;
        }

        var json = JsonSerializer.Serialize(payload, Flux2ProJson);
        using var submitRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://api.novita.ai/v3/async/flux-2-pro"))
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var submitResp = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResp.Content.ReadAsStringAsync(cancellationToken);

        if (!submitResp.IsSuccessStatusCode)
            throw new Exception($"{submitResp.StatusCode}: {submitRaw}");

        var taskId = ReadTaskId(submitRaw);
        var taskResultJson = await PollTaskResultJson(taskId, cancellationToken);
        var (status, reason, imageUrls) = ReadImageUrls(taskResultJson);

        if (!string.Equals(status, "TASK_STATUS_SUCCEED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Novita Flux 2 Pro task not successful (status={status}): {reason}\n{taskResultJson}");

        if (imageUrls.Count == 0)
            throw new Exception("Novita Flux 2 Pro returned no images.");

        var images = new List<string>();
        foreach (var url in imageUrls)
            images.Add(await DownloadAsDataUrlAsync(url, cancellationToken));

        Dictionary<string, JsonElement>? providerMetadata = new()
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new { task_id = taskId }, JsonSerializerOptions.Web)
        };

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonDocument.Parse(taskResultJson).RootElement.Clone()
            }
        };
    }

    public static bool IsFlux2ProModel(string? model)
        => string.Equals(model, "flux-2-pro", StringComparison.OrdinalIgnoreCase);
}
