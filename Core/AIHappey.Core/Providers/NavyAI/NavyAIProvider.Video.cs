using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NavyAI;

public partial class NavyAIProvider
{
    private const string NavyVideoOperationPrefix = "navv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var payload = NavyCopyOptions(request.ProviderOptions);
        payload["model"] = request.Model; payload["prompt"] = request.Prompt; payload["sync"] = false;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["size"] = request.Resolution;
        if (request.Duration is not null) payload["seconds"] = request.Duration.Value;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        var images = new List<string>();
        if (request.Image is not null) images.Add(NavyVideoImage(request.Image));
        images.AddRange((request.InputReferences ?? []).Select(NavyVideoImage));
        if (request.FrameImages is not null) images.AddRange(request.FrameImages.Select(x => NavyVideoImage(x.Image)));
        if (images.Count > 0) payload["image_url"] = images.Count == 1 ? images[0] : images.Take(5).ToArray();

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        { Content = new StringContent(JsonSerializer.Serialize(payload, NavyImageJson), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(message, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        NavyEnsureSuccess(response, raw, "video submission");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = NavyJobId(root) ?? throw new InvalidOperationException("NavyAI video submission returned no job id.");
        var warnings = new List<object>();
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        if (images.Count > 5) warnings.Add(new { type = "unsupported", feature = "inputReferences", details = "Only five references were sent." });
        return new VideoOperationStartResult
        {
            Operation = EncodeNavyVideoOperation(id, request.Model), Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var data = DecodeNavyVideoOperation(operation);
        var task = await PollNavyMediaTaskAsync(data.Id, cancellationToken);
        var response = new HeaderResponseData
        { Timestamp = DateTime.UtcNow, Headers = task.Headers, ModelId = data.Model.ToModelId(GetIdentifier()) };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(task.Root);
        if (NavyIsFailure(task.Root)) return new VideoOperationErrorResult
        { Error = NavyGetString(task.Root, "error") ?? NavyGetString(task.Root, "message") ?? $"NavyAI video job '{data.Id}' failed.", ProviderMetadata = metadata, Response = response };
        if (!NavyIsTerminal(task.Root)) return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };
        var videos = await ResolveNavyMediaAsync(task.Root, true, cancellationToken);
        if (videos.Count == 0) return new VideoOperationErrorResult
        { Error = $"NavyAI video job '{data.Id}' completed without video output.", ProviderMetadata = metadata, Response = response };
        return new VideoOperationCompletedResult
        {
            Videos = videos.Select(x => new VideoOperationVideoData { Type = "base64", MediaType = x.MediaType, Data = x.Base64 }),
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static string NavyVideoImage(VideoFile image)
        => image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? image.Data : $"data:{image.MediaType};base64,{image.Data}";

    private static string EncodeNavyVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new NavyVideoOperation(id, model), NavyImageJson);
        return NavyVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static NavyVideoOperation DecodeNavyVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(NavyVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A valid model-aware NavyAI video operation token is required.", nameof(operation));
        try
        {
            var value = operation[NavyVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<NavyVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), NavyImageJson);
            if (result is null || string.IsNullOrWhiteSpace(result.Id) || string.IsNullOrWhiteSpace(result.Model)) throw new JsonException();
            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new ArgumentException("The NavyAI video operation token is invalid.", nameof(operation), exception); }
    }

    private sealed record NavyVideoOperation(string Id, string Model);
}
