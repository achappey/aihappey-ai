using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Runway;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider : IModelProvider
{
   
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ExtractTaskId(JsonNode? json)
            => json?["id"]?.ToString() ?? throw new Exception("No task ID returned from Runway API.");

    public Task<string> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        
        var now = DateTime.UtcNow;
        var metadata = imageRequest.GetImageProviderMetadata<RunwayImageProviderMetadata>(GetIdentifier());
        var payload = new
        {
            promptText = imageRequest.Prompt,
            model = imageRequest.Model,
            ratio = imageRequest.Size ?? "1024:1024",
            seed = imageRequest.Seed,
            contentModeration = metadata?.ContentModeration,
            referenceImages = imageRequest.Files?.Select(a => new
            {
                uri = a.Data.ToDataUrl(a.MediaType)
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync("v1/text_to_image",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {text}");

        var node = JsonNode.Parse(text);
        var taskId = ExtractTaskId(node);
        var images = await WaitForTaskAndUploadAsync(taskId, cancellationToken);

        return new()
        {
            Images = images,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }

    /// <summary>
    /// Polls a Runway task until completion and uploads all output URLs to MCP storage.
    /// </summary>
    public async Task<IEnumerable<string>> WaitForTaskAndUploadAsync(
        string taskId,
        CancellationToken ct = default)
    {
        string? status = null;
        JsonNode? json;
        List<string> results = [];

        do
        {
            using var resp = await _client.GetAsync($"v1/tasks/{taskId}", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{resp.StatusCode}: {text}");

            json = JsonNode.Parse(text);
            status = json?["status"]?.ToString();

            if (status == "SUCCEEDED")
            {
                var outputs = json?["output"]?.AsArray();
                if (outputs == null || outputs.Count == 0)
                    throw new Exception("No outputs returned by Runway API.");

                foreach (var output in outputs)
                {
                    var outputUrl = output?.ToString();
                    if (string.IsNullOrWhiteSpace(outputUrl))
                        continue;

                    var allItems = await _client.GetAsync(outputUrl, ct);
                    var result = await allItems.Content.ReadAsByteArrayAsync(ct);

                    results.Add(Convert.ToBase64String(result).ToDataUrl(allItems.Content.Headers.ContentType?.MediaType!));
                }

                if (results.Count == 0)
                    throw new Exception("No valid outputs could be uploaded.");

                return results;
            }

            if (status == "FAILED")
            {
                var reason = json?["failure"]?.ToString() ?? "Unknown failure.";
                var code = json?["failureCode"]?.ToString() ?? "";
                throw new Exception($"Runway task failed: {reason} ({code})");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);

        } while (status != "SUCCEEDED" && status != "FAILED");

        throw new TimeoutException($"Runway task {taskId} did not complete.");
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
