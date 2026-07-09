using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const string GoogleTranscriptionPrompt = "Generate a transcript of the speech. Do not include any other text.";

    private static readonly JsonSerializerOptions GoogleTranscriptionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("Media type is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var model = NormalizeGoogleTranscriptionModelId(request.Model);
        var audioData = NormalizeGoogleTranscriptionAudioData(request.Audio);
        var payload = BuildGoogleTranscriptionPayload(model, audioData, request.MediaType);
        var requestBody = payload.ToJsonString(GoogleTranscriptionJsonOptions);

        var googleOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (googleOptions.ValueKind == JsonValueKind.Object && googleOptions.EnumerateObject().Any())
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "providerOptions.google",
                reason = "Google transcription uses a fixed native Interactions API payload."
            });
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{Google} transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = ExtractGoogleTranscriptionText(root);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No transcription text returned.");

        return new TranscriptionResponse
        {
            Text = text,
            Segments = [],
            Warnings = warnings,
            ProviderMetadata = BuildGoogleTranscriptionProviderMetadata(root, model),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }

    private static JsonObject BuildGoogleTranscriptionPayload(string model, string audioData, string mediaType)
        => new()
        {
            ["model"] = model,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = GoogleTranscriptionPrompt
                },
                new JsonObject
                {
                    ["type"] = "audio",
                    ["data"] = audioData,
                    ["mime_type"] = mediaType
                }
            }
        };

    private Dictionary<string, JsonElement> BuildGoogleTranscriptionProviderMetadata(JsonElement root, string model)
    {
        var providerMetadata = new Dictionary<string, JsonElement>();
        var googleMetadata = new Dictionary<string, JsonElement>();

        if (TryGetProperty(root, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            googleMetadata["usage"] = usage.Clone();

        providerMetadata[GetIdentifier()] = JsonSerializer.SerializeToElement(googleMetadata, JsonSerializerOptions.Web);

        if (usage.ValueKind != JsonValueKind.Object)
            return providerMetadata;

        var normalizedUsage = NormalizeGoogleUsage(usage, null);
        var serviceTier = TryGetString(root, "service_tier") ?? TryGetString(root, "serviceTier");
        var pricing = GoogleTieredPricingResolver.Resolve(
            model,
            serviceTier,
            ModelCostMetadataEnricher.GetTotalTokens(normalizedUsage));

        var costMetadata = ModelCostMetadataEnricher.AddCostFromUsage(
            normalizedUsage,
            existingMetadata: null,
            pricing);

        if (TryGetGatewayMetadata(costMetadata, out var gateway))
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(gateway, JsonSerializerOptions.Web);

        return providerMetadata;
    }

    private static string NormalizeGoogleTranscriptionModelId(string model)
    {
        var text = model.Trim();

        const string modelsPrefix = "models/";
        if (text.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[modelsPrefix.Length..];

        var providerPrefix = GoogleExtensions.Identifier() + "/";
        if (text.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[providerPrefix.Length..];

        return text;
    }

    private static string NormalizeGoogleTranscriptionAudioData(object audio)
    {
        var audioData = audio switch
        {
            string value => value,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? string.Empty,
            JsonElement json => json.ToString(),
            _ => audio.ToString() ?? string.Empty
        };

        audioData = audioData.RemoveDataUrlPrefix();

        if (string.IsNullOrWhiteSpace(audioData))
            throw new ArgumentException("Audio data is required.", nameof(audio));

        return audioData;
    }

    private static string ExtractGoogleTranscriptionText(JsonElement root)
    {
        if (TryReadGoogleTranscriptionString(root, "output_text", out var outputText))
            return outputText;

        if (TryReadGoogleTranscriptionString(root, "outputText", out var outputTextCamel))
            return outputTextCamel;

        if (TryReadGoogleTranscriptionString(root, "text", out var text))
            return text;

        var textParts = new List<string>();
        CollectGoogleTranscriptionTextParts(root, textParts);

        return string.Join("\n", textParts.Where(static part => !string.IsNullOrWhiteSpace(part))).Trim();
    }

    private static void CollectGoogleTranscriptionTextParts(JsonElement element, ICollection<string> textParts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isTextContent = TryReadGoogleTranscriptionString(element, "type", out var type)
                    && string.Equals(type, "text", StringComparison.OrdinalIgnoreCase);

                if (isTextContent && TryReadGoogleTranscriptionString(element, "text", out var text))
                    textParts.Add(text);

                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "input", StringComparison.OrdinalIgnoreCase))
                        continue;

                    CollectGoogleTranscriptionTextParts(property.Value, textParts);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectGoogleTranscriptionTextParts(item, textParts);
                break;
        }
    }

    private static bool TryReadGoogleTranscriptionString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;

        if (!TryGetProperty(element, propertyName, out var property))
            return false;

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.ToString(),
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(value);
    }
}
