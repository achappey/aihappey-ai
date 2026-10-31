using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.AbacusAI;

public sealed partial class AbacusAIProvider
{
    private static Dictionary<string, JsonElement> AdditionalMediaOptions(Dictionary<string, JsonElement>? properties)
        => new() { ["abacusai"] = JsonSerializer.SerializeToElement(properties ?? []) };

    private static SpeechRequest SpeechFromOpenAI(AudioSpeechRequest options) => new()
    {
        Model = options.Model, Text = options.Input, Voice = options.Voice,
        OutputFormat = options.ResponseFormat, Instructions = options.Instructions, Speed = options.Speed,
        ProviderOptions = AdditionalMediaOptions(options.AdditionalProperties)
    };

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        var response = await SpeechRequest(SpeechFromOpenAI(options), cancellationToken);
        return (Convert.FromBase64String(response.Audio.Base64), response.Audio.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options.StreamFormat is not null and not "sse")
            throw new NotSupportedException("AbacusAI streaming speech uses SSE.");
        var speech = SpeechFromOpenAI(options);
        await foreach (var part in SpeechStreamingAsync(new()
        {
            Model = speech.Model, Text = [new() { Text = speech.Text }], Voice = speech.Voice,
            OutputFormat = speech.OutputFormat is null ? null : new() { Type = speech.OutputFormat },
            Instructions = speech.Instructions, Speed = speech.Speed, ProviderOptions = speech.ProviderOptions
        }, cancellationToken))
        {
            if (part is SpeechAudioPart audio)
                yield return new AudioSpeechStreamDelta { Audio = audio.Audio };
            else if (part is SpeechFinishPart finish)
                yield return new AudioSpeechStreamDone
                {
                    Usage = new()
                    {
                        InputTokens = (int?)finish.Usage?.InputTokens, OutputTokens = (int?)finish.Usage?.OutputTokens,
                        TotalTokens = finish.Usage?.Raw?.TryGetValue("total_tokens", out var total) == true ? total.GetInt32() : null
                    }
                };
        }
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.File);
        if (options.ResponseFormat is not null and not "json" and not "text"
            || options.TimestampGranularities is { Length: > 0 } || options.Include is { Length: > 0 }
            || options.ChunkingStrategy is not null || options.KnownSpeakerNames is { Length: > 0 }
            || options.KnownSpeakerReferences is { Length: > 0 })
            throw new NotSupportedException("AbacusAI chat-based transcription does not provide timestamps, diarization or transcription logprobs; use json or text.");
        var rawOptions = JsonSerializer.SerializeToNode(options.AdditionalProperties ?? [])!.AsObject();
        if (options.Language is not null)
            rawOptions["language"] = options.Language;
        if (options.Prompt is not null)
            rawOptions["prompt"] = options.Prompt;
        if (options.Temperature is not null)
            rawOptions["temperature"] = options.Temperature;
        var response = await TranscriptionRequest(new()
        {
            Model = options.Model, Audio = await FormBase64(options.File, cancellationToken),
            MediaType = string.IsNullOrWhiteSpace(options.File.ContentType)
                ? Path.GetExtension(options.File.FileName).TrimStart('.') : options.File.ContentType,
            ProviderOptions = new() { ["abacusai"] = JsonSerializer.SerializeToElement(rawOptions) }
        }, cancellationToken);
        var root = (JsonElement)response.Response.Body!;
        OpenAITranscriptionUsage? usage = null;
        if (root.TryGetProperty("usage", out var u)
            && ReadInt64(u, "prompt_tokens") is { } input && ReadInt64(u, "completion_tokens") is { } output)
            usage = new OpenAITranscriptionTokenUsage
            {
                InputTokens = input, OutputTokens = output, TotalTokens = ReadInt64(u, "total_tokens") ?? input + output
            };
        return new OpenAITranscriptionResponse { Text = response.Text, Usage = usage };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Replay only the completed transcript, never fabricated native partials.
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new OpenAITranscriptionTextDone { Text = response.Text, Usage = (response as OpenAITranscriptionResponse)?.Usage };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.SerializeToNode(options, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var providerOptions = OpenAIImageOptions(config, options.AdditionalProperties);
        var response = await ImageRequest(new()
        {
            Model = options.Model, Prompt = options.Prompt, N = options.N, Size = options.Size, ProviderOptions = providerOptions
        }, cancellationToken);
        return OpenAIImageResponse(response);
    }

    public async Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        var files = new List<ImageFile>();
        foreach (var reference in options.Images ?? [])
            files.Add(new ImageFileUrl { Url = reference.ImageUrl ?? throw new NotSupportedException("AbacusAI image editing requires image_url, not file_id.") });
        foreach (var file in options.ImageFiles ?? [])
            files.Add(new() { Data = await FormBase64(file, cancellationToken), MediaType = file.ContentType });
        if (files.Count == 0)
            throw new ArgumentException("At least one image is required for image editing.");
        var config = JsonSerializer.SerializeToNode(options, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var response = await ImageRequest(new()
        {
            Model = options.Model, Prompt = options.Prompt, N = options.N, Size = options.Size, Files = files,
            Mask = options.Mask is not null || options.MaskFile is not null
                ? throw new NotSupportedException("Supply a documented model-specific mask in image_config; AbacusAI has no documented common mask parameter.") : null,
            ProviderOptions = OpenAIImageOptions(config, options.AdditionalProperties)
        }, cancellationToken);
        return OpenAIImageResponse(response);
    }

    private static Dictionary<string, JsonElement> OpenAIImageOptions(JsonObject serialized, Dictionary<string, JsonElement>? extra)
    {
        var config = new JsonObject();
        foreach (var name in new[] { "background", "moderation", "output_compression", "output_format", "quality", "style", "input_fidelity" })
            if (serialized[name] is { } value)
                config[name] = value.DeepClone();
        var raw = JsonSerializer.SerializeToNode(extra ?? [])!.AsObject();
        if (raw["image_config"] is JsonObject explicitConfig)
            MergeMediaOptions(config, explicitConfig);
        raw["image_config"] = config;
        return new() { ["abacusai"] = JsonSerializer.SerializeToElement(raw) };
    }

    private static OpenAIImagesResponse OpenAIImageResponse(ImageResponse response) => new()
    {
        Created = new DateTimeOffset(response.Response.Timestamp).ToUnixTimeSeconds(),
        Data = response.Images!.Select(image => new OpenAIImageData { B64Json = image[(image.IndexOf(',') + 1)..] }).ToList(),
        Usage = response.Usage is { } usage ? new()
        {
            InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens
        } : null
    };

    private static async Task<string> FormBase64(IFormFile file, CancellationToken ct)
    {
        await using var input = file.OpenReadStream();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, ct);
        return Convert.ToBase64String(output.ToArray());
    }
}
