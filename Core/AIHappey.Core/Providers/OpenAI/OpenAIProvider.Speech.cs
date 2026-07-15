using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private static readonly JsonSerializerOptions OpenAiSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<OpenAiSpeechProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var voice = !string.IsNullOrEmpty(request.Voice) ? request.Voice
            : !string.IsNullOrEmpty(metadata?.Voice)
            ? metadata.Voice : "alloy";

        var formatString = request.OutputFormat ?? metadata?.ResponseFormat ?? "mp3";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = voice,
            ["response_format"] = formatString,
            ["speed"] = request.Speed ?? metadata?.Speed,
            ["instructions"] = request.Instructions ?? metadata?.Instructions
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenAiSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var raw = Encoding.UTF8.GetString(audioBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpenAI speech request failed ({(int)response.StatusCode})."
                : $"OpenAI speech request failed ({(int)response.StatusCode}): {raw}");
        }

        var base64 = Convert.ToBase64String(audioBytes);
        var modelItem = GetOpenAiSpeechCatalogModel(request.Model);
        var providerMetadata = BuildProviderMetadata(request.Text, modelItem);

        return new SpeechResponse()
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse()
            {
                Base64 = base64,
                MimeType = response.Content.Headers.ContentType?.MediaType ?? MapToAudioMimeType(formatString),
                Format = formatString
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
            },
        };
    }

    private static Dictionary<string, JsonElement> BuildProviderMetadata(string text, Model? modelItem)
    {
        var providerMetadata = Constants.OpenAI.CreatePrimitiveProviderMetadata();
        var inputPricePerCharacter = modelItem?.Pricing?.Input;

        if (inputPricePerCharacter is null or <= 0m)
            return providerMetadata;

        var inputCharacters = text.Length;

        var inputCost = Math.Round(
            inputCharacters * inputPricePerCharacter.Value,
            8,
            MidpointRounding.AwayFromZero);

        return Constants.OpenAI.CreatePrimitiveProviderMetadata(costs: inputCost);
    }

    private static Model? GetOpenAiSpeechCatalogModel(string model)
        => Constants.OpenAI.GetModels()
            .FirstOrDefault(item => item.Id.EndsWith(model, StringComparison.OrdinalIgnoreCase));

    public static string MapToAudioMimeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "application/octet-stream";

        return value.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
    }


}
