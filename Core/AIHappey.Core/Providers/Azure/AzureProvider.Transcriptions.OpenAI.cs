using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
       OpenAITranscriptionRequest options,
       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);

        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
     OpenAITranscriptionRequest options,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.File);

        if (options.File.Length == 0)
            throw new InvalidOperationException("The uploaded audio file is empty.");

        await using var fileStream = options.File.OpenReadStream();

        using var audioFormat = ResolveAudioFormat(options.File);

        using var pullStream = AudioInputStream.CreatePullStream(
            new StreamPullAudioInputStreamCallback(fileStream),
            audioFormat);

        using var audioConfig = AudioConfig.FromStreamInput(pullStream);

        var speechConfig = SpeechConfig.FromSubscription(
            GetKey(),
            GetEndpointRegion());

        speechConfig.SetProfanity(ProfanityOption.Raw);

        if (!string.IsNullOrWhiteSpace(options.Language))
            speechConfig.SpeechRecognitionLanguage = options.Language;

        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var channel = Channel.CreateUnbounded<IOpenAITranscriptionStreamEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        var transcript = new StringBuilder();

        var recognitionCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        recognizer.Recognized += (_, eventArgs) =>
        {
            if (eventArgs.Result.Reason != ResultReason.RecognizedSpeech)
                return;

            var text = eventArgs.Result.Text;

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (transcript.Length > 0)
                transcript.Append(' ');

            transcript.Append(text);

            channel.Writer.TryWrite(
                new OpenAITranscriptionTextDelta
                {
                    Delta = text,
                    SegmentId = eventArgs.Result.ResultId
                });
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

        var producer = ProduceEventsAsync();

        await foreach (var streamEvent in channel.Reader.ReadAllAsync(cancellationToken))
            yield return streamEvent;

        await producer;

        async Task ProduceEventsAsync()
        {
            var started = false;

            try
            {
                await recognizer
                    .StartContinuousRecognitionAsync()
                    .WaitAsync(cancellationToken);

                started = true;

                await recognitionCompleted.Task.WaitAsync(cancellationToken);

                channel.Writer.TryWrite(
                    new OpenAITranscriptionTextDone
                    {
                        Text = transcript.ToString()
                    });

                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        await recognizer
                            .StopContinuousRecognitionAsync()
                            .WaitAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // Recognition is already stopped or canceled.
                    }
                }
            }
        }
    }

    private sealed class StreamPullAudioInputStreamCallback(Stream stream) : PullAudioInputStreamCallback
    {
        private readonly Stream _stream = stream;

        public override int Read(byte[] dataBuffer, uint size)
        {
            var count = Math.Min(dataBuffer.Length, checked((int)size));

            return _stream.Read(
                dataBuffer,
                offset: 0,
                count);
        }

        public override void Close()
        {
            // De caller beheert de lifecycle van de IFormFile-stream.
        }
    }

    private static AudioStreamFormat ResolveAudioFormat(IFormFile file)
    {
        var extension = Path
            .GetExtension(file.FileName)
            .ToLowerInvariant();

        return extension switch
        {
            ".mp3" => AudioStreamFormat.GetCompressedFormat(
                AudioStreamContainerFormat.MP3),

            ".ogg" or ".opus" => AudioStreamFormat.GetCompressedFormat(
                AudioStreamContainerFormat.OGG_OPUS),

            ".flac" => AudioStreamFormat.GetCompressedFormat(
                AudioStreamContainerFormat.FLAC),

            ".m4a" or ".mp4" or ".webm" =>
                AudioStreamFormat.GetCompressedFormat(
                    AudioStreamContainerFormat.ANY),

            ".wav" => AudioStreamFormat.GetCompressedFormat(
                AudioStreamContainerFormat.ANY),

            _ => AudioStreamFormat.GetCompressedFormat(
                AudioStreamContainerFormat.ANY)
        };
    }
}

