using OpenAI.Audio;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var audioClient = new AudioClient(
               request.Model,
               GetKey()
           );

        var metadata = request.GetProviderMetadata<OpenAiSpeechProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var voice = !string.IsNullOrEmpty(request.Voice) ? new GeneratedSpeechVoice(request.Voice)
            : !string.IsNullOrEmpty(metadata?.Voice)
            ? new GeneratedSpeechVoice(metadata?.Voice) : GeneratedSpeechVoice.Alloy;

        var formatString = request.OutputFormat ?? metadata?.ResponseFormat ?? "mp3";
        var format = new GeneratedSpeechFormat(formatString);

        var result = await audioClient.GenerateSpeechAsync(request.Text,
            voice,
            new SpeechGenerationOptions()
            {
                SpeedRatio = request.Speed ?? metadata?.Speed,
                Instructions = request.Instructions ?? metadata?.Instructions,
                ResponseFormat = format
            },
            cancellationToken);

        var base64 = Convert.ToBase64String(result.Value.ToArray());
        var modelItem = await this.GetModel(request.Model, cancellationToken);
        var providerMetadata = BuildProviderMetadata(request.Text, modelItem);

        return new SpeechResponse()
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse()
            {
                Base64 = base64,
                MimeType = MapToAudioMimeType(formatString),
                Format = formatString
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            },
        };
    }

    private static Dictionary<string, JsonElement> BuildProviderMetadata(string text, Model? modelItem)
    {
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [Constants.OpenAI] = JsonSerializer.SerializeToElement(new { }, JsonSerializerOptions.Web)
        };

        var inputPricePerCharacter = modelItem?.Pricing?.Input;

        if (inputPricePerCharacter is null or <= 0m)
            return providerMetadata;

        var inputCharacters = text.Length;

        var inputCost = Math.Round(
            inputCharacters * inputPricePerCharacter.Value,
            8,
            MidpointRounding.AwayFromZero);

        providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
        {
            cost = inputCost
        }, JsonSerializerOptions.Web);

        return providerMetadata;
    }

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
            _ => "application/octet-stream"
        };
    }


}
