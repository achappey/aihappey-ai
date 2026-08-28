using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Radient;

public partial class RadientProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = DecodeBase64(request.Audio?.ToString());
        var options = request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var metadata)
            && metadata.ValueKind == JsonValueKind.Object ? metadata : default;
        var openAiRequest = new OpenAITranscriptionRequest
        {
            Model = StripProviderPrefix(request.Model),
            File = new FormFile(new MemoryStream(bytes, false), 0, bytes.Length, "file", "audio" + AudioExtension(request.MediaType))
            {
                ContentType = request.MediaType,
                Headers = new HeaderDictionary()
            },
            Prompt = ReadString(options, "prompt"),
            ResponseFormat = ReadString(options, "response_format") ?? "json",
            Language = ReadString(options, "language"),
            Temperature = ReadFloat(options, "temperature"),
            AdditionalProperties = options.ValueKind == JsonValueKind.Object
                ? options.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone()) : null
        };
        var response = await OpenAITranscriptionRequestAsync(openAiRequest, cancellationToken);
        return new TranscriptionResponse
        {
            Text = response.Text,
            Language = openAiRequest.Language,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = response
            }
        };
    }

    private static byte[] DecodeBase64(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) value = value[(marker + 8)..];
        return Convert.FromBase64String(value);
    }
    private static string AudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    { "audio/mpeg" => ".mp3", "audio/wav" or "audio/x-wav" => ".wav", "audio/mp4" => ".m4a", "audio/ogg" => ".ogg", _ => ".audio" };
    private static string? ReadString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static float? ReadFloat(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : null;
}
