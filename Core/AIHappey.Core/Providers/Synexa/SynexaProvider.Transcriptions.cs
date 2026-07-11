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

        var metadata = request.GetProviderMetadata<SynexaTranscriptionProviderMetadata>(GetIdentifier());

        var audioString = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioString))
            throw new InvalidOperationException("Audio input is required.");

        var audioBase64 = audioString.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? audioString.RemoveDataUrlPrefix()
            : audioString;

        var input = new Dictionary<string, object?>
        {
            ["audio"] = $"data:{request.MediaType};base64,{audioBase64}",
            ["language"] = metadata?.Language,
            ["translate"] = metadata?.Translate,
            ["temperature"] = metadata?.Temperature
        };

        var prediction = await CreatePredictionAsync(request.Model, input, cancellationToken);
        var completed = await WaitPredictionAsync(prediction, metadata?.Wait, cancellationToken);

        var text = ExtractOutputText(completed.Output);
        if (string.IsNullOrWhiteSpace(text) && completed.Output.ValueKind == JsonValueKind.Object)
        {
            if (completed.Output.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                text = textEl.GetString() ?? string.Empty;
        }

        return new TranscriptionResponse
        {
            Text = text ?? string.Empty,
            Language = metadata?.Language,
            Segments = [],
            Warnings = [],
            Request = new()
            {
                Body = JsonSerializer.Serialize(input, JsonSerializerOptions.Web)
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
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
}

