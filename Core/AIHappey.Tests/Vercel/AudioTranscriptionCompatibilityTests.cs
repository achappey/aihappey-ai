using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AIHappey.Tests.Vercel;

public sealed class AudioTranscriptionCompatibilityTests
{
    [Fact]
    public async Task OpenAI_multipart_form_maps_to_vercel_transcription_request_with_provider_options()
    {
        var form = CreateForm(new Dictionary<string, StringValues>
        {
            ["model"] = "async/async_asr_v1.0",
            ["language"] = "en",
            ["prompt"] = "domain terms",
            ["temperature"] = "0.2",
            ["timestamp_granularities[]"] = new StringValues(["word", "segment"])
        });

        var openAiRequest = form.ToAudioTranscriptionRequest();
        openAiRequest.ValidateOpenAITranscriptionRequest();

        var vercelRequest = await openAiRequest.ToTranscriptionRequest("async_asr_v1.0", "async");

        Assert.Equal("async_asr_v1.0", vercelRequest.Model);
        Assert.Equal("audio/wav", vercelRequest.MediaType);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")), vercelRequest.Audio);

        Assert.NotNull(vercelRequest.ProviderOptions);
        var providerOptions = vercelRequest.ProviderOptions!["async"];
        Assert.Equal("en", providerOptions.GetProperty("language").GetString());
        Assert.Equal("domain terms", providerOptions.GetProperty("prompt").GetString());
        Assert.Equal(0.2, providerOptions.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(["word", "segment"], providerOptions.GetProperty("timestamp_granularities").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public void OpenAI_transcription_response_formats_are_projected_from_vercel_response()
    {
        var response = new TranscriptionResponse
        {
            Text = "hello world",
            Language = "en",
            DurationInSeconds = 1.2f,
            Segments =
            [
                new TranscriptionSegment
                {
                    Text = "hello world",
                    StartSecond = 0,
                    EndSecond = 1.2f
                }
            ]
        };

        var json = JsonSerializer.Serialize(response.ToOpenAITranscriptionResponse("json"), JsonSerializerOptions.Web);
        var verbose = JsonSerializer.Serialize(response.ToOpenAITranscriptionResponse("verbose_json"), JsonSerializerOptions.Web);

        Assert.Contains("\"text\":\"hello world\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("durationInSeconds", json, StringComparison.Ordinal);
        Assert.Contains("\"task\":\"transcribe\"", verbose, StringComparison.Ordinal);
        Assert.Contains("\"segments\"", verbose, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAI_transcription_request_rejects_unsupported_features()
    {
        var diarized = CreateForm(new Dictionary<string, StringValues>
        {
            ["model"] = "gpt-4o-transcribe-diarize",
            ["response_format"] = "diarized_json"
        }).ToAudioTranscriptionRequest();

        var include = CreateForm(new Dictionary<string, StringValues>
        {
            ["model"] = "gpt-4o-transcribe",
            ["include[]"] = "logprobs"
        }).ToAudioTranscriptionRequest();

        Assert.Throws<NotSupportedException>(diarized.ValidateOpenAITranscriptionRequest);
        Assert.Throws<NotSupportedException>(include.ValidateOpenAITranscriptionRequest);
    }

    [Fact]
    public void Naive_streaming_events_match_openai_transcription_event_names()
    {
        var delta = JsonSerializer.Serialize(new AudioTranscriptionTextDelta
        {
            Delta = "hello world"
        }, JsonSerializerOptions.Web);

        var done = JsonSerializer.Serialize(new AudioTranscriptionTextDone
        {
            Text = "hello world"
        }, JsonSerializerOptions.Web);

        Assert.Contains("\"type\":\"transcript.text.delta\"", delta, StringComparison.Ordinal);
        Assert.Contains("\"delta\":\"hello world\"", delta, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"transcript.text.done\"", done, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"hello world\"", done, StringComparison.Ordinal);
    }

    private static FormCollection CreateForm(Dictionary<string, StringValues> fields)
    {
        var bytes = Encoding.UTF8.GetBytes("fake audio");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

        return new FormCollection(fields, new FormFileCollection { file });
    }
}
