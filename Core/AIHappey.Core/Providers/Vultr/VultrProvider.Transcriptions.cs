using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        if (!request.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Vultr transcription requires an audio media type.", nameof(request));

        var audio = DecodeAudio(request.Audio);
        var options = new OpenAITranscriptionRequest
        {
            Model = request.Model,
            File = new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio")
            {
                Headers = new HeaderDictionary(),
                ContentType = request.MediaType
            }
        };
        ApplyVultrTranscriptionOptions(options, request.ProviderOptions);

        var result = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        var response = new TranscriptionResponse
        {
            Text = result.Text,
            Segments = [],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = new Dictionary<string, string>()
            }
        };

        if (result is OpenAITranscriptionVerboseResponse verbose)
        {
            response.Language = verbose.Language;
            response.DurationInSeconds = (float)verbose.Duration;
            response.Segments = verbose.Segments?.Select(segment => new TranscriptionSegment
            {
                Text = segment.Text,
                StartSecond = (float)segment.Start,
                EndSecond = (float)segment.End
            }).ToArray() ?? [];
        }
        else if (result is OpenAITranscriptionDiarizedResponse diarized)
        {
            response.DurationInSeconds = (float)diarized.Duration;
            response.Segments = diarized.Segments.Select(segment => new TranscriptionSegment
            {
                Text = segment.Text,
                StartSecond = (float)segment.Start,
                EndSecond = (float)segment.End
            }).ToArray();
        }

        return response;
    }

    private static byte[] DecodeAudio(object? value)
    {
        var encoded = value?.ToString();
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ArgumentException("Audio data is required.", nameof(value));

        var dataMarker = encoded.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (dataMarker < 0)
                throw new ArgumentException("Audio data URLs must contain base64 data.", nameof(value));
            encoded = encoded[(dataMarker + ";base64,".Length)..];
        }

        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be valid base64 data.", nameof(value), exception);
        }
    }

    private static void ApplyVultrTranscriptionOptions(
        OpenAITranscriptionRequest request,
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue("vultr", out var options))
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.vultr must be an object.", nameof(providerOptions));

        foreach (var option in options.EnumerateObject())
        {
            switch (option.Name)
            {
                case "language" when option.Value.ValueKind == JsonValueKind.String:
                    request.Language = option.Value.GetString();
                    break;
                case "prompt" when option.Value.ValueKind == JsonValueKind.String:
                    request.Prompt = option.Value.GetString();
                    break;
                case "response_format" when option.Value.ValueKind == JsonValueKind.String:
                    request.ResponseFormat = option.Value.GetString();
                    break;
                case "temperature" when option.Value.ValueKind == JsonValueKind.Number
                    && option.Value.TryGetSingle(out var temperature):
                    request.Temperature = temperature;
                    break;
                case "timestamp_granularities" when option.Value.ValueKind == JsonValueKind.Array:
                    request.TimestampGranularities = option.Value.EnumerateArray()
                        .Select(value => value.ValueKind == JsonValueKind.String
                            ? value.GetString()!
                            : throw new ArgumentException("timestamp_granularities must contain strings.", nameof(providerOptions)))
                        .ToArray();
                    break;
                default:
                    throw new NotSupportedException($"Vultr transcription does not support provider option '{option.Name}' or its value.");
            }
        }
    }
}
