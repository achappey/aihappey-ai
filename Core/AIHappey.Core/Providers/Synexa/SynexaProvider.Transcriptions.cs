using System.Text.Json;
using AIHappey.Common.Model.Providers.Synexa;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var audioString = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioString))
            throw new InvalidOperationException("Audio input is required.");

        var audioBase64 = audioString.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? audioString.RemoveDataUrlPrefix()
            : audioString;

        var input = new Dictionary<string, object?>
        {
            ["audio"] = $"data:{request.MediaType};base64,{audioBase64}",
            ["language"] = TryGetSynexaProperty(metadata, "language"),
            ["translate"] = TryGetSynexaProperty(metadata, "translate"),
            ["temperature"] = TryGetSynexaProperty(metadata, "temperature")
        };
        MergeSynexaInputMetadata(input, metadata, "audio", "language", "translate", "temperature");

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var completed = await WaitPredictionAsync(prediction, GetSynexaWaitOptions(metadata), cancellationToken);

        var text = ExtractOutputText(completed.Output);
        if (string.IsNullOrWhiteSpace(text) && completed.Output.ValueKind == JsonValueKind.Object)
        {
            if (completed.Output.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                text = textEl.GetString() ?? string.Empty;
        }

        return new TranscriptionResponse
        {
            Text = text ?? string.Empty,
            Language = TryGetSynexaString(metadata, "language"),
            Segments = [],
            Warnings = [],
            Request = new()
            {
                Body = JsonSerializer.Serialize(input, JsonSerializerOptions.Web)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(CreateSynexaPredictionMetadata(completed)),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = completed.Output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? null
                    : completed.Output.Clone()
            }
        };
    }

    private static object? TryGetSynexaProperty(JsonElement metadata, string name)
        => metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;

    private static string? TryGetSynexaString(JsonElement metadata, string name)
        => metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}

