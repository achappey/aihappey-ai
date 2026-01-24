using AIHappey.Common.Model.Providers.Azure;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = string.IsNullOrWhiteSpace(request.Model)
            ? "speech-to-text"
            : request.Model.Contains('/')
                ? request.Model.SplitModelId().Model
                : request.Model;

        var now = DateTime.UtcNow;
        var bytes = GetAudioBytes(request);

        var metadata = request.GetProviderMetadata<AzureTranscriptionProviderMetadata>(GetIdentifier());

        var format = AudioStreamFormat.GetWaveFormatPCM(
            samplesPerSecond: metadata?.SamplesPerSecond != null ? (uint)metadata.SamplesPerSecond : (uint)16000,
            bitsPerSample: metadata?.BitsPerSample != null ? (byte)metadata.BitsPerSample : (byte)16,
            channels: metadata?.Channels != null ? (byte)metadata.Channels : (byte)1);

        var pushStream = AudioInputStream.CreatePushStream(format);
        using var audioConfig = AudioConfig.FromStreamInput(pushStream);

        var config = SpeechConfig.FromSubscription(GetKey(), GetEndpointRegion());
        config.SetProfanity(ProfanityOption.Raw);

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            config.SpeechRecognitionLanguage = metadata.Language;

        pushStream.Write(bytes);
        pushStream.Close();

        using var recognizer = new SpeechRecognizer(config, audioConfig);
        var result = await recognizer.RecognizeOnceAsync().WaitAsync(cancellationToken);

        if (result.Reason == ResultReason.RecognizedSpeech)
        {
            return new TranscriptionResponse
            {
                Text = result.Text ?? string.Empty,
                Language = metadata?.Language,
                DurationInSeconds = (float)result.Duration.TotalSeconds,
                Warnings = [],
                Segments = [],
                Response = new()
                {
                    Timestamp = now,
                    ModelId = modelId,
                    Body = new
                    {
                        reason = result.Reason.ToString(),
                        durationSeconds = result.Duration.TotalSeconds,
                        text = result.Text
                    }
                }
            };
        }

        if (result.Reason == ResultReason.NoMatch)
        {
            var details = NoMatchDetails.FromResult(result);
            throw new InvalidOperationException($"NoMatch: {details.Reason}");
        }

        if (result.Reason == ResultReason.Canceled)
        {
            var c = CancellationDetails.FromResult(result);
            throw new InvalidOperationException(
                $"Canceled: {c.Reason}; ErrorCode={c.ErrorCode}; Details={c.ErrorDetails}");
        }

        throw new InvalidOperationException($"Recognition failed: {result.Reason}");
    }

    private static byte[] GetAudioBytes(TranscriptionRequest request)
    {
        var audio = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio))
            throw new InvalidOperationException("No audio provided.");

        audio = audio.Trim();

        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            const string base64Marker = ";base64,";
            var idx = audio.IndexOf(base64Marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                throw new InvalidOperationException("Audio data-url is missing ';base64,'.");

            audio = audio[(idx + base64Marker.Length)..];
        }

        try
        {
            return Convert.FromBase64String(audio);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }
    }
}

