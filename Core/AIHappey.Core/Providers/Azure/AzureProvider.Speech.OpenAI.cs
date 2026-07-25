using AIHappey.Core.AI;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
     AudioSpeechRequest options,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var speechConfig = SpeechConfig.FromSubscription(
            GetKey(),
            GetEndpointRegion());

        if (!string.IsNullOrWhiteSpace(options.Voice))
            speechConfig.SpeechSynthesisVoiceName = options.Voice;

        speechConfig.SetSpeechSynthesisOutputFormat(
            ResolveSpeechSynthesisOutputFormat(options.ResponseFormat));

        var channel = Channel.CreateUnbounded<IAudioSpeechStreamEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        using var callback = new AzureSpeechOutputStreamCallback(channel.Writer);
        using var outputStream = AudioOutputStream.CreatePushStream(callback);
        using var audioConfig = AudioConfig.FromStreamOutput(outputStream);
        using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig);

        var producerTask = ProduceAsync();

        await foreach (var streamEvent in channel.Reader.ReadAllAsync(cancellationToken))
            yield return streamEvent;

        await producerTask;

        async Task ProduceAsync()
        {
            try
            {
                var result = await synthesizer
                    .SpeakTextAsync(options.Input)
                    .WaitAsync(cancellationToken);

                if (result.Reason != ResultReason.SynthesizingAudioCompleted)
                {
                    var details = SpeechSynthesisCancellationDetails.FromResult(result);

                    throw new InvalidOperationException(
                        $"Azure speech synthesis failed. " +
                        $"Reason={result.Reason}; " +
                        $"ErrorCode={details.ErrorCode}; " +
                        $"Details={details.ErrorDetails}");
                }

                channel.Writer.TryWrite(
                    new AudioSpeechStreamDone
                    {
                        Usage = null
                    });

                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        }
    }

    private sealed class AzureSpeechOutputStreamCallback(
        ChannelWriter<IAudioSpeechStreamEvent> writer)
        : PushAudioOutputStreamCallback
    {
        private readonly ChannelWriter<IAudioSpeechStreamEvent> _writer = writer;

        public override uint Write(byte[] dataBuffer)
        {
            if (dataBuffer.Length == 0)
                return 0;

            var audio = Convert.ToBase64String(dataBuffer);

            return _writer.TryWrite(
                new AudioSpeechStreamDelta
                {
                    Audio = audio
                })
                ? checked((uint)dataBuffer.Length)
                : 0;
        }

        public override void Close()
        {
        }
    }

    private static SpeechSynthesisOutputFormat ResolveSpeechSynthesisOutputFormat(
        string? responseFormat)
    {
        return responseFormat?.Trim().ToLowerInvariant() switch
        {
            null or "" or "mp3" =>
                SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3,

            "wav" =>
                SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm,

            "pcm" =>
                SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm,

            "opus" =>
                SpeechSynthesisOutputFormat.Ogg24Khz16BitMonoOpus,

            _ => throw new NotSupportedException(
                $"Azure speech streaming does not support response format '{responseFormat}'.")
        };
    }
}

