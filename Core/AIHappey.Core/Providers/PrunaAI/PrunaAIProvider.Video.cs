using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.PrunaAI;

public partial class PrunaAIProvider
{
    private const string PrunaVideoOperationTokenPrefix = "pav1_";
    private sealed record PrunaVideoOperationData(string PredictionId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        var input = CreatePrunaInput(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        input["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) input["aspect_ratio"] = request.AspectRatio;
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });

        var root = await SendPrunaPredictionAsync(request.Model, input, false, cancellationToken);
        var predictionId = GetPrunaString(root, "id")
            ?? throw new InvalidOperationException("Pruna video prediction response did not contain an id.");
        return new VideoOperationStartResult
        {
            Operation = EncodePrunaVideoOperation(predictionId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A video operation is required.", nameof(operation));
        var operationData = DecodePrunaVideoOperation(operation);
        var root = await GetPrunaPredictionAsync(operationData.PredictionId, cancellationToken);
        var status = GetPrunaString(root, "status") ?? "processing";
        var model = GetPrunaString(root, "model") ?? operationData.Model;
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = GetPrunaError(root), ProviderMetadata = metadata, Response = response };
        if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var url = GetPrunaString(root, "generation_url", "output_url");
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult { Error = "Pruna video prediction succeeded without a generation URL.", ProviderMetadata = metadata, Response = response };
        var (bytes, mediaType) = await DownloadPrunaOutputAsync(url, "video/mp4", cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", MediaType = mediaType, Data = Convert.ToBase64String(bytes) }],
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static string EncodePrunaVideoOperation(string predictionId, string model)
    {
        var json = JsonSerializer.Serialize(new PrunaVideoOperationData(predictionId, model), PrunaJson);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return PrunaVideoOperationTokenPrefix + token;
    }

    private static PrunaVideoOperationData DecodePrunaVideoOperation(string operation)
    {
        if (!operation.StartsWith(PrunaVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new PrunaVideoOperationData(Uri.UnescapeDataString(operation), null);
        var token = operation[PrunaVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        var padding = token.Length % 4;
        if (padding != 0) token = token.PadRight(token.Length + 4 - padding, '=');
        try
        {
            var data = JsonSerializer.Deserialize<PrunaVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(token)), PrunaJson);
            if (data is null || string.IsNullOrWhiteSpace(data.PredictionId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Pruna video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The Pruna video operation token is invalid.", nameof(operation), ex);
        }
    }

}
