using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Respeecher;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Respeecher;

public sealed class RespeecherProviderTests
{
    private static readonly string[] Models = ["en-rt", "ua-rt", "ar-rt", "de-rt", "pt-br-rt"];
    private const string Voices = """
        [{"id":"voice/one","full_name":"One","gender":"female","accent":"native","age":30,
          "sampling_params":{"temperature":0.6,"future_parameter":true},"future_metadata":{"enabled":true}},
         {"id":"voice/one","full_name":"Duplicate"}]
        """;
    private const string Chunks = """
        {"type":"chunk","data":"AID//w=="}

        {"type":"chunk","data":"AAD/fw=="}
        """;

    [Fact]
    public async Task DiscoveryUsesFiveDocumentedModelPathsAndPreservesUsableVoices()
    {
        var paths = new List<string>();
        var provider = Create(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
            {
                paths.Add(message.RequestUri!.AbsolutePath);
                return Json(Voices);
            }
            var body = await Body(message, token);
            Assert.Equal("voice/one", body.GetProperty("voice").GetProperty("id").GetString());
            Assert.Contains(message.RequestUri!.AbsolutePath,
                Models.Select(model => $"/v1/public/tts/{model}/tts/bytes"));
            return Wav();
        });
        var discovered = (await provider.ListModels()).ToArray();
        Assert.Equal(Models.Select(model => $"/v1/public/tts/{model}/voices"), paths);
        Assert.Equal(Models.Select(model => $"respeecher/{model}/voice/one"), discovered.Select(model => model.Id));
        foreach (var model in discovered)
        {
            Assert.Equal("One", model.Name);
            Assert.Equal("speech", model.Type);
            Assert.Equal("Respeecher", model.OwnedBy);
            Assert.Null(model.Pricing);
            Assert.Null(model.ContextWindow);
            var result = await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
            Assert.Equal(model.Id, result.Response.ModelId);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingKeyReturnsNoModelsAndRejectsAllSpeech(string? key)
    {
        var provider = Create(NoNetwork, key);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAISpeechRequestAsync(OpenAI("wav")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(provider.OpenAISpeechStreamingAsync(OpenAI())));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":123}]")]
    [InlineData("[{\"id\":\" \"}]")]
    [InlineData("not json")]
    public async Task MalformedDiscoveryFailsWithoutFallback(string body)
        => await Assert.ThrowsAnyAsync<JsonException>(() => Create((_, _) => Task.FromResult(Json(body))).ListModels());

    [Fact]
    public async Task EmptyDiscoveryRemainsEmptyAcrossAllLanguages()
    {
        var requests = 0;
        Assert.Empty(await Create((_, _) =>
        {
            requests++;
            return Task.FromResult(Json("[]"));
        }).ListModels());
        Assert.Equal(5, requests);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("http-response")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("json")]
    [InlineData("malformed")]
    public async Task DiscoveryKeepsOtherLanguagesAndDiscardsEntireFailedLanguage(string failure)
    {
        var calls = 0;
        var provider = Create((_, _) =>
        {
            calls++;
            if (calls == 2)
                return DiscoveryFailure(failure);
            return Task.FromResult(Json(Voices));
        });
        var discovered = (await provider.ListModels()).ToArray();
        Assert.Equal(5, calls);
        Assert.Equal(Models.Where(model => model != "ua-rt").Select(model => $"respeecher/{model}/voice/one"),
            discovered.Select(model => model.Id));
    }

    [Fact]
    public async Task DiscoveryContinuesAfterMultipleDifferentFailures()
    {
        var calls = 0;
        var provider = Create((_, _) => ++calls switch
        {
            1 => DiscoveryFailure("network"),
            2 => DiscoveryFailure("timeout"),
            3 => DiscoveryFailure("malformed"),
            4 => DiscoveryFailure("http"),
            _ => Task.FromResult(Json(Voices))
        });
        Assert.Equal("respeecher/pt-br-rt/voice/one", Assert.Single(await provider.ListModels()).Id);
        Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task DiscoveryAuthFailureAfterSuccessPropagatesImmediately(HttpStatusCode status)
    {
        var calls = 0;
        var provider = Create((_, _) => Task.FromResult(++calls == 1 ? Json(Voices) : Json("denied", status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DiscoveryCallerCancellationAfterSuccessIsNeverSwallowed()
    {
        using var source = new CancellationTokenSource();
        var calls = 0;
        var provider = Create((_, token) =>
        {
            if (++calls == 2)
            {
                source.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(Json(Voices));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(source.Token));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("http-response")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("json")]
    [InlineData("malformed")]
    public async Task CompleteDiscoveryOutageRethrowsFirstFailure(string failure)
    {
        var calls = 0;
        Exception? first = null;
        var provider = Create(async (_, _) =>
        {
            calls++;
            try { return await DiscoveryFailure(failure); }
            catch (Exception error) { first ??= error; throw; }
        });
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => provider.ListModels());
        Assert.Equal(5, calls);
        if (first is not null)
            Assert.Same(first, actual);
        else if (failure != "http-response")
            Assert.IsAssignableFrom<JsonException>(actual);
        if (failure is "http" or "http-response")
            Assert.Equal(HttpStatusCode.ServiceUnavailable, Assert.IsType<HttpRequestException>(actual).StatusCode);
    }

    [Fact]
    public async Task SuccessfulEmptyLanguageCountsAsSuccessDespiteOtherFailures()
    {
        var calls = 0;
        var provider = Create((_, _) => ++calls == 3 ? Task.FromResult(Json("[]")) : DiscoveryFailure("network"));
        Assert.Empty(await provider.ListModels());
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task StandardSpeechMapsTextAndVoiceWithoutMutatingNativeOptions()
    {
        var request = Request();
        request.Model = "respeecher/en-rt/voice/one";
        request.Voice = "alloy";
        request.OutputFormat = "wav";
        request.ProviderOptions = new()
        {
            ["respeecher"] = Element(new
            {
                transcript = "wrong",
                voice = new { id = "wrong", sampling_params = new { temperature = 0.6, future_sampling = new[] { 1, 2 } }, future_voice = true },
                output_format = new { sample_rate = 16000, future_output = true },
                future_option = new { enabled = true }
            }),
            ["other"] = Element(new { unrelated = true })
        };
        var original = request.ProviderOptions["respeecher"].GetRawText();
        var provider = Create(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.respeecher.com/v1/public/tts/en-rt/tts/bytes", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("transcript").GetString());
            var voice = body.GetProperty("voice");
            Assert.Equal("voice/one", voice.GetProperty("id").GetString());
            Assert.Equal(0.6, voice.GetProperty("sampling_params").GetProperty("temperature").GetDouble());
            Assert.Equal(2, voice.GetProperty("sampling_params").GetProperty("future_sampling").GetArrayLength());
            Assert.True(voice.GetProperty("future_voice").GetBoolean());
            Assert.Equal(16000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            Assert.True(body.GetProperty("output_format").GetProperty("future_output").GetBoolean());
            Assert.True(body.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            foreach (var field in new[] { "model", "stream", "unrelated", "speed", "instructions" })
                Assert.False(body.TryGetProperty(field, out _));
            return Wav(16000);
        });
        var response = await provider.SpeechRequest(request);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal(WavBytes(16000), Convert.FromBase64String(response.Audio.Base64));
        Assert.Equal("respeecher/en-rt/voice/one", response.Response.ModelId);
        Assert.Empty(response.Warnings);
        Assert.Null(response.ProviderMetadata);
        Assert.Equal(original, request.ProviderOptions["respeecher"].GetRawText());
        Assert.Equal("alloy", request.Voice);
        Assert.Equal("respeecher/en-rt/voice/one", request.Model);
    }

    [Fact]
    public async Task OpenAIAdditionalPropertiesPassThroughOnBothSurfacesWithoutDefaultsOrMutation()
    {
        var options = OpenAI("wav");
        options.Voice = "alloy";
        options.AdditionalProperties = new()
        {
            ["transcript"] = Element("wrong"),
            ["voice"] = Element(new { id = "wrong", sampling_params = new { future_sampling = true } }),
            ["output_format"] = Element(new { sample_rate = 24000 }),
            ["future_option"] = Element(new[] { "a", "b" })
        };
        var original = Element(options.AdditionalProperties).GetRawText();
        var provider = Create(async (message, token) =>
        {
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("transcript").GetString());
            Assert.Equal("voice-one", body.GetProperty("voice").GetProperty("id").GetString());
            Assert.True(body.GetProperty("voice").GetProperty("sampling_params").GetProperty("future_sampling").GetBoolean());
            Assert.Equal(24000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            if (message.RequestUri!.AbsolutePath.EndsWith("/bytes"))
                Assert.False(body.GetProperty("output_format").TryGetProperty("encoding", out _));
            else
                Assert.Equal("pcm_s16le", body.GetProperty("output_format").GetProperty("encoding").GetString());
            Assert.Equal(2, body.GetProperty("future_option").GetArrayLength());
            Assert.Equal(4, body.EnumerateObject().Count());
            return message.RequestUri!.AbsolutePath.EndsWith("/bytes") ? Wav() : Json(Chunks);
        });
        var audio = await provider.OpenAISpeechRequestAsync(options);
        Assert.Equal("audio/wav", audio.MimeType);
        Assert.Equal(WavBytes(), audio.Audio);
        options.ResponseFormat = "pcm";
        Assert.Equal(3, (await Drain(provider.OpenAISpeechStreamingAsync(options))).Count);
        Assert.Equal(original, Element(options.AdditionalProperties).GetRawText());
    }

    [Fact]
    public async Task DeserializedOpenAIProviderOptionsPreserveNativeVoiceControlsAndDeterministicPrecedence()
    {
        var options = JsonSerializer.Deserialize<AudioSpeechRequest>("""
            {
              "model":"respeecher/en-rt/voice/one","input":"Hello","voice":"alloy","stream_format":"sse","response_format":"wav",
              "transcript":"direct-wrong","output_format":{"sample_rate":11111},"future_option":{"source":"direct"},
              "direct_extension":{"enabled":true},
              "providerOptions":{
                "respeecher":{
                  "transcript":"envelope-wrong",
                  "voice":{"id":"native-wrong","sampling_params":{"temperature":0.6,"future_sampling":{"enabled":true}},"future_voice":true},
                  "output_format":{"sample_rate":24000,"future_output":true},
                  "future_option":{"source":"envelope"}
                },
                "other":{"unrelated":true}
              }
            }
            """)!;
        var original = JsonSerializer.Serialize(options);
        var calls = 0;
        var provider = Create(async (message, token) =>
        {
            calls++;
            AssertAuth(message);
            var body = await Body(message, token);
            Assert.Equal("Hello", body.GetProperty("transcript").GetString());
            var voice = body.GetProperty("voice");
            Assert.Equal("voice/one", voice.GetProperty("id").GetString());
            Assert.Equal(0.6, voice.GetProperty("sampling_params").GetProperty("temperature").GetDouble());
            Assert.True(voice.GetProperty("sampling_params").GetProperty("future_sampling").GetProperty("enabled").GetBoolean());
            Assert.True(voice.GetProperty("future_voice").GetBoolean());
            var format = body.GetProperty("output_format");
            Assert.Equal(24000, format.GetProperty("sample_rate").GetInt32());
            Assert.True(format.GetProperty("future_output").GetBoolean());
            if (message.RequestUri!.AbsolutePath.EndsWith("/bytes"))
                Assert.False(format.TryGetProperty("encoding", out _));
            else
                Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
            Assert.Equal("envelope", body.GetProperty("future_option").GetProperty("source").GetString());
            Assert.True(body.GetProperty("direct_extension").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("providerOptions", out _));
            Assert.False(body.TryGetProperty("unrelated", out _));
            Assert.Equal(5, body.EnumerateObject().Count());
            return message.RequestUri!.AbsolutePath.EndsWith("/bytes") ? Wav() : Json(Chunks);
        });
        var audio = await provider.OpenAISpeechRequestAsync(options);
        Assert.Equal("audio/wav", audio.MimeType);
        options.ResponseFormat = "pcm";
        Assert.Equal(3, (await Drain(provider.OpenAISpeechStreamingAsync(options))).Count);
        Assert.Equal(2, calls);
        options.ResponseFormat = "wav";
        Assert.Equal(original, JsonSerializer.Serialize(options));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"respeecher\":null}")]
    [InlineData("{\"respeecher\":123}")]
    public async Task MalformedDeserializedOpenAIProviderOptionsAreRejected(string envelope)
    {
        var options = JsonSerializer.Deserialize<AudioSpeechRequest>(
            "{\"model\":\"respeecher/en-rt/voice-one\",\"input\":\"Hello\",\"stream_format\":\"sse\",\"response_format\":\"wav\",\"providerOptions\":" + envelope + "}")!;
        var provider = Create(NoNetwork);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAISpeechRequestAsync(options));
        options.ResponseFormat = "pcm";
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pcm_f32le")]
    [InlineData("pcm_s16le")]
    [InlineData("pcm_mulaw")]
    public async Task StreamingReadsFragmentedJsonLinesAndEmitsDeltasAndDoneWithoutUsage(string? encoding)
    {
        var options = OpenAI();
        if (encoding is not null)
            options.AdditionalProperties = new() { ["output_format"] = Element(new { encoding, sample_rate = 8000 }) };
        var provider = Create(async (message, token) =>
        {
            Assert.Equal("https://api.respeecher.com/v1/public/tts/en-rt/tts/sse", message.RequestUri!.AbsoluteUri);
            AssertAuth(message);
            var body = await Body(message, token);
            Assert.Equal("pcm_s16le", body.GetProperty("output_format").GetProperty("encoding").GetString());
            Assert.Equal(24000, body.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
            return new(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(Chunks))) };
        });
        var events = await Drain(provider.OpenAISpeechStreamingAsync(options));
        Assert.Equal(3, events.Count);
        Assert.Equal(new byte[] { 0, 128, 255, 255 }, Convert.FromBase64String(Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio));
        Assert.Equal(new byte[] { 0, 0, 255, 127 }, Convert.FromBase64String(Assert.IsType<AudioSpeechStreamDelta>(events[1]).Audio));
        Assert.Null(Assert.IsType<AudioSpeechStreamDone>(events[2]).Usage);
    }

    [Fact]
    public async Task StandardPcmTranslationNormalizesEncodingAndRateWhilePreservingOtherFields()
    {
        var options = OpenAI();
        options.ResponseFormat = "pcm";
        options.AdditionalProperties = new() { ["output_format"] = Element(new { encoding = "pcm_mulaw", sample_rate = 16000, future = true }) };
        var original = Element(options.AdditionalProperties).GetRawText();
        var provider = Create(async (message, token) =>
        {
            var format = (await Body(message, token)).GetProperty("output_format");
            Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
            Assert.Equal(24000, format.GetProperty("sample_rate").GetInt32());
            Assert.True(format.GetProperty("future").GetBoolean());
            return Json(Chunks);
        });
        await Drain(provider.OpenAISpeechStreamingAsync(options));
        Assert.Equal(original, Element(options.AdditionalProperties).GetRawText());
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("opus")]
    [InlineData("future")]
    public async Task UnsupportedStandardFormatsFailWithoutNetwork(string format)
    {
        var provider = Create(NoNetwork);
        var request = Request();
        request.OutputFormat = format;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        var options = OpenAI("wav");
        options.ResponseFormat = format;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Fact]
    public async Task WavStreamingAndPcmBytesAreRejected()
    {
        var provider = Create(NoNetwork);
        var options = OpenAI();
        options.ResponseFormat = "wav";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
        options.ResponseFormat = "pcm";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
    }

    [Fact]
    public async Task UnsupportedSpeedInstructionsLanguageAndStreamFormatAreNotSilentlyIgnored()
    {
        var provider = Create(NoNetwork);
        var request = Request();
        request.Speed = 1;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        request.Speed = null;
        request.Instructions = "Speak softly";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        request.Instructions = null;
        request.Language = "de";
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        var options = OpenAI("wav");
        options.Speed = 1;
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
        options.ResponseFormat = "pcm";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
        options.Speed = null;
        options.Instructions = "Softly";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
        options.Instructions = null;
        options.StreamFormat = "audio";
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData("respeecher/../voice")]
    [InlineData("respeecher/./voice")]
    [InlineData("respeecher/%2e%2e/voice")]
    [InlineData("respeecher/en-rt?x=1/voice")]
    [InlineData("respeecher/en-rt#fragment/voice")]
    [InlineData("respeecher/en\\rt/voice")]
    [InlineData("respeecher//voice")]
    [InlineData("respeecher/en-rt/")]
    [InlineData("en-rt")]
    public async Task UnsafeOrIncompleteModelIdsNeverReachNetwork(string model)
    {
        var request = Request();
        request.Model = model;
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoNetwork).SpeechRequest(request));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"wrong\"")]
    public async Task MalformedNativeOptionsAreRejected(string native)
    {
        var request = Request();
        request.ProviderOptions = new() { ["respeecher"] = JsonSerializer.Deserialize<JsonElement>(native) };
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoNetwork).SpeechRequest(request));
    }

    [Theory]
    [InlineData("voice", "123")]
    public async Task MalformedNativeNestedObjectsAreRejected(string field, string json)
    {
        var options = OpenAI("wav");
        options.AdditionalProperties = new() { [field] = JsonSerializer.Deserialize<JsonElement>(json) };
        await Assert.ThrowsAsync<ArgumentException>(() => Create(NoNetwork).OpenAISpeechRequestAsync(options));
        options.ResponseFormat = "pcm";
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoNetwork).OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData("{\"encoding\":\"future_native_encoding\",\"sample_rate\":12345,\"future_output\":true}")]
    [InlineData("\"future_native_format\"")]
    [InlineData("123")]
    [InlineData("null")]
    public async Task NativeOutputFormatPassesThroughUnchangedOnNativeSpeechSurface(string json)
    {
        var native = JsonSerializer.Deserialize<JsonElement>(json);
        var request = Request();
        request.ProviderOptions = new() { ["respeecher"] = Element(new { output_format = native }) };
        var original = native.GetRawText();
        var provider = Create(async (message, token) =>
        {
            Assert.Equal(original, (await Body(message, token)).GetProperty("output_format").GetRawText());
            return message.RequestUri!.AbsolutePath.EndsWith("/bytes") ? Wav() : Json(Chunks);
        });
        await provider.SpeechRequest(request);
        Assert.Equal(original, request.ProviderOptions["respeecher"].GetProperty("output_format").GetRawText());
    }

    [Fact]
    public async Task StandardPcmTranslationRequiresNativeOutputFormatObjectToPreserveItsFields()
    {
        var options = OpenAI();
        options.ResponseFormat = "pcm";
        options.AdditionalProperties = new() { ["output_format"] = Element("future_native_format") };
        await Assert.ThrowsAsync<ArgumentException>(() => Drain(Create(NoNetwork).OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mp3")]
    [InlineData("opus")]
    [InlineData("aac")]
    [InlineData("flac")]
    [InlineData("")]
    public async Task OpenAIRequiresExplicitSupportedFormatBeforeNetwork(string? format)
    {
        var options = OpenAI();
        options.ResponseFormat = format;
        var provider = Create(NoNetwork);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Fact]
    public async Task OpenAIDefaultMp3IsNotSilentlyReturnedAsWavOrPcm()
    {
        var options = new AudioSpeechRequest { Model = "respeecher/en-rt/voice-one", Input = "Hello", StreamFormat = "sse" };
        var provider = Create(NoNetwork);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(options));
        await Assert.ThrowsAsync<NotSupportedException>(() => Drain(provider.OpenAISpeechStreamingAsync(options)));
    }

    [Theory]
    [InlineData("pcm_f32le")]
    [InlineData("pcm_s16le")]
    [InlineData("pcm_mulaw")]
    [InlineData("wav")]
    [InlineData(null)]
    public async Task OpenAIWavRejectsNativeEncodingBeforeNetwork(string? encoding)
    {
        var options = OpenAI("wav");
        options.AdditionalProperties = new() { ["output_format"] = Element(new { encoding, sample_rate = 16000 }) };
        var original = JsonSerializer.Serialize(options);
        await Assert.ThrowsAsync<NotSupportedException>(() => Create(NoNetwork).OpenAISpeechRequestAsync(options));
        Assert.Equal(original, JsonSerializer.Serialize(options));
    }

    [Fact]
    public async Task OpenAIWavPreservesNativeSampleRateAndRealWavBytes()
    {
        var options = OpenAI("wav");
        options.AdditionalProperties = new() { ["output_format"] = Element(new { sample_rate = 16000, future = true }) };
        var provider = Create(async (message, token) =>
        {
            var format = (await Body(message, token)).GetProperty("output_format");
            Assert.Equal(16000, format.GetProperty("sample_rate").GetInt32());
            Assert.True(format.GetProperty("future").GetBoolean());
            Assert.False(format.TryGetProperty("encoding", out _));
            return Wav(16000);
        });
        var response = await provider.OpenAISpeechRequestAsync(options);
        Assert.Equal("audio/wav", response.MimeType);
        Assert.Equal(WavBytes(16000), response.Audio);
        Assert.Equal(16000, BitConverter.ToInt32(response.Audio, 24));
        Assert.Equal(16, BitConverter.ToInt16(response.Audio, 34));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(response.Audio, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(response.Audio, 8, 4));
    }

    [Fact]
    public async Task HttpFailuresPropagateWithoutDiscoveryFallbackOrDone()
    {
        var provider = Create((_, _) => Task.FromResult(Json("denied", HttpStatusCode.Forbidden)));
        var discovery = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.Forbidden, discovery.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(Request()));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.OpenAISpeechRequestAsync(OpenAI("wav")));
        var events = new List<IAudioSpeechStreamEvent>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(OpenAI()))
                events.Add(item);
        });
        Assert.Empty(events);
    }

    [Fact]
    public async Task InBandErrorAfterAudioThrowsWithNativeStatusAndNeverEmitsDone()
    {
        var provider = Create((_, _) => Task.FromResult(Json(
            "{\"type\":\"chunk\",\"data\":\"AQI=\"}\n{\"type\":\"error\",\"error\":\"failed\",\"status_code\":429,\"context_id\":\"native\"}\n")));
        var events = new List<IAudioSpeechStreamEvent>();
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in provider.OpenAISpeechStreamingAsync(OpenAI()))
                events.Add(item);
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("native", error.Message);
        Assert.IsType<AudioSpeechStreamDelta>(Assert.Single(events));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"type\":\"chunk\"}")]
    [InlineData("{\"type\":\"chunk\",\"data\":123}")]
    [InlineData("{\"type\":\"unexpected\",\"data\":\"AQI=\"}")]
    public async Task MalformedStreamFailsWithoutDone(string body)
        => await Assert.ThrowsAsync<JsonException>(() => Drain(Create((_, _) => Task.FromResult(Json(body))).OpenAISpeechStreamingAsync(OpenAI())));

    [Fact]
    public async Task InvalidBase64AndEmptyAudioFail()
    {
        await Assert.ThrowsAsync<FormatException>(() => Drain(Create((_, _) => Task.FromResult(Json(
            "{\"type\":\"chunk\",\"data\":\"invalid\"}"))).OpenAISpeechStreamingAsync(OpenAI())));
        foreach (var body in new[] { "", "\n", "{\"type\":\"chunk\",\"data\":\"\"}" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Drain(Create((_, _) => Task.FromResult(Json(body))).OpenAISpeechStreamingAsync(OpenAI())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) })).SpeechRequest(Request()));
    }

    [Fact]
    public async Task PreCancellationStopsAllSurfacesBeforeNetwork()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var provider = Create(NoNetwork);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SpeechRequest(Request(), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAISpeechRequestAsync(OpenAI("wav"), source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(provider.OpenAISpeechStreamingAsync(OpenAI(), source.Token)));
    }

    [Fact]
    public async Task MidStreamCancellationNeverEmitsDoneEvenWhenReaderHasBufferedData()
    {
        using var source = new CancellationTokenSource();
        var provider = Create((_, _) => Task.FromResult(Json(Chunks)));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(OpenAI(), source.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<AudioSpeechStreamDelta>(enumerator.Current);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task CancellationInterruptsPendingNativeRead()
    {
        using var source = new CancellationTokenSource();
        var stream = new PendingStream();
        var provider = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        await using var enumerator = provider.OpenAISpeechStreamingAsync(OpenAI(), source.Token).GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task CredentialsRemainPerRequestAndPerProvider()
    {
        var observed = new List<string>();
        var handler = new Handler((message, _) =>
        {
            observed.Add(Assert.Single(message.Headers.GetValues("X-API-Key")));
            Assert.Null(message.Headers.Authorization);
            Assert.Empty(message.RequestUri!.Query);
            return Task.FromResult(message.Method == HttpMethod.Get ? Json("[]") : Wav());
        });
        var factory = new Factory(handler);
        var first = new RespeecherProvider(new Resolver("first-key"), factory);
        var second = new RespeecherProvider(new Resolver("second-key"), factory);
        await first.SpeechRequest(Request());
        await second.SpeechRequest(Request());
        await first.ListModels();
        Assert.Equal(new[] { "first-key", "second-key" }.Concat(Enumerable.Repeat("first-key", 5)), observed);
    }

    [Fact]
    public async Task HeaderScopedRegistrationResolvesAndEveryDiscoveredEntryCanExecute()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Respeecher-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("RESPEECHER"));
        Assert.Equal("X-Respeecher-Key", HeaderApiKeyResolver.SupportedProviderHeaders["respeecher"]);
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services,
            item => item.ImplementationType == typeof(RespeecherProvider)).Lifetime);
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory(new Handler(async (message, token) =>
        {
            AssertAuth(message);
            if (message.Method == HttpMethod.Get)
                return Json(Voices);
            var body = await Body(message, token);
            Assert.Equal("voice/one", body.GetProperty("voice").GetProperty("id").GetString());
            Assert.Contains(message.RequestUri!.AbsolutePath,
                Models.Select(model => $"/v1/public/tts/{model}/tts/bytes"));
            return Wav();
        })));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("respeecher"));
        var provider = Assert.IsType<RespeecherProvider>(registry.GetModelProvider(scope.ServiceProvider, "respeecher"));
        Assert.Same(scope.ServiceProvider.GetRequiredService<RespeecherProvider>(), provider);
        foreach (var model in await provider.ListModels())
            await provider.SpeechRequest(new() { Model = model.Id, Text = "Hello" });
    }

    [Fact]
    public async Task HeaderCredentialsAreIsolatedBetweenRequestSnapshots()
    {
        var firstContext = new DefaultHttpContext();
        firstContext.Request.Headers["X-Respeecher-Key"] = "first-key";
        var secondContext = new DefaultHttpContext();
        secondContext.Request.Headers["X-OpenAI-Key"] = "unrelated-key";
        var firstResolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new RequestContextAccessor(firstContext)));
        var secondResolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new RequestContextAccessor(secondContext)));
        Assert.Equal("first-key", firstResolver.Resolve("respeecher"));
        Assert.Null(secondResolver.Resolve("respeecher"));
        var missing = new RespeecherProvider(secondResolver, new Factory(new Handler(NoNetwork)));
        Assert.Empty(await missing.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SpeechRequest(Request()));
    }

    private static SpeechRequest Request() => new() { Model = "respeecher/en-rt/voice-one", Text = "Hello" };
    private static AudioSpeechRequest OpenAI(string format = "pcm") => new() { Model = "respeecher/en-rt/voice-one", Input = "Hello", StreamFormat = "sse", ResponseFormat = format };
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task<JsonElement> Body(HttpRequestMessage message, CancellationToken token)
        => JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token));
    private static async Task<List<IAudioSpeechStreamEvent>> Drain(IAsyncEnumerable<IAudioSpeechStreamEvent> stream)
    {
        var result = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in stream)
            result.Add(item);
        return result;
    }
    private static void AssertAuth(HttpRequestMessage message)
    {
        Assert.Equal("test-key", Assert.Single(message.Headers.GetValues("X-API-Key")));
        Assert.Null(message.Headers.Authorization);
        Assert.Empty(message.RequestUri!.Query);
    }
    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("Unexpected network request.");
    private static RespeecherProvider Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder, string? key = "test-key")
        => new(new Resolver(key), new Factory(new Handler(responder)));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Task<HttpResponseMessage> DiscoveryFailure(string failure) => failure switch
    {
        "http" => throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable),
        "http-response" => Task.FromResult(Json("unavailable", HttpStatusCode.ServiceUnavailable)),
        "network" => throw new HttpRequestException("connection failed"),
        "timeout" => throw new TaskCanceledException("request timed out", new TimeoutException()),
        "json" => Task.FromResult(Json("not json")),
        "malformed" => Task.FromResult(Json("[{\"id\":\"must-not-leak\"},{}]")),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
    private static HttpResponseMessage Wav(int sampleRate = 22050)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(WavBytes(sampleRate)) };
    private static byte[] WavBytes(int sampleRate = 22050)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(44);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(8);
        writer.Write(new byte[] { 0, 128, 255, 255, 0, 0, 255, 127 });
        return stream.ToArray();
    }
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "respeecher" ? key : null;
    }
    private sealed class RequestContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
    private sealed class PendingStream : MemoryStream
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
