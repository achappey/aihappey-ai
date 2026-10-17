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
            samplesPerSecond: metadata?.SamplesPerSecond != null ? (uint)metadata.SamplesPerSecond : 16000,
            bitsPerSample: metadata?.BitsPerSample != null ? (byte)metadata.BitsPerSample : (byte)16,
            channels: metadata?.Channels != null ? (byte)metadata.Channels : (byte)1);

        using var pushStream = AudioInputStream.CreatePushStream(format);
        using var audioConfig = AudioConfig.FromStreamInput(pushStream);

        var config = SpeechConfig.FromSubscription(GetKey(), GetEndpointRegion());
        config.SetProfanity(ProfanityOption.Raw);

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            config.SpeechRecognitionLanguage = metadata.Language;

        pushStream.Write(bytes);
        pushStream.Close();

        using var recognizer = new SpeechRecognizer(config, audioConfig);
        var recognizedTexts = new List<string>();
        var duration = TimeSpan.Zero;
        string? noMatchReason = null;
        var recognitionCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        recognizer.Recognized += (_, eventArgs) =>
        {
            if (eventArgs.Result.Reason == ResultReason.RecognizedSpeech
                && !string.IsNullOrWhiteSpace(eventArgs.Result.Text))
            {
                recognizedTexts.Add(eventArgs.Result.Text);
                duration += eventArgs.Result.Duration;
            }
            else if (eventArgs.Result.Reason == ResultReason.NoMatch)
            {
                noMatchReason = NoMatchDetails.FromResult(eventArgs.Result).Reason.ToString();
            }
        };

        recognizer.Canceled += (_, eventArgs) =>
        {
            if (eventArgs.Reason == CancellationReason.Error)
            {
                recognitionCompleted.TrySetException(
                    new InvalidOperationException(
                        $"Azure speech recognition canceled. " +
                        $"ErrorCode={eventArgs.ErrorCode}; " +
                        $"Details={eventArgs.ErrorDetails}"));
            }
            else
            {
                recognitionCompleted.TrySetResult();
            }
        };

        recognizer.SessionStopped += (_, _) =>
        {
            recognitionCompleted.TrySetResult();
        };

        var startAttempted = false;

        try
        {
            startAttempted = true;
            await recognizer.StartContinuousRecognitionAsync().WaitAsync(cancellationToken);
            await recognitionCompleted.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (startAttempted && !recognitionCompleted.Task.IsCompleted)
            {
                try
                {
                    await recognizer.StopContinuousRecognitionAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Preserve the recognition or cancellation failure.
                }
            }
        }

        if (recognizedTexts.Count == 0)
            throw new InvalidOperationException($"NoMatch: {noMatchReason ?? "No speech recognized."}");

        var text = CombineTranscriptionTexts(recognizedTexts);
        return new TranscriptionResponse
        {
            Text = text,
            Language = metadata?.Language,
            DurationInSeconds = (float)duration.TotalSeconds,
            Warnings = [],
            Segments = [],
            Response = new()
            {
                Timestamp = now,
                ModelId = modelId,
                Body = new
                {
                    reason = ResultReason.RecognizedSpeech.ToString(),
                    durationSeconds = duration.TotalSeconds,
                    text
                }
            }
        };
    }

    private static string CombineTranscriptionTexts(IEnumerable<string> recognizedTexts)
        => string.Join(
            " ",
            recognizedTexts
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()));

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
