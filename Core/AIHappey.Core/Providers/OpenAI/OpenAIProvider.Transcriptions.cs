using AIHappey.Core.AI;
using OpenAI.Audio;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Extensions;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<TranscriptionResponse> TranscribeWithDiarization(
        TranscriptionRequest request,
        CancellationToken ct = default)
    {
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GetKey());

        using var form = new MultipartFormDataContent();

        // audio file
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType =
            new MediaTypeHeaderValue(request.MediaType);

        form.Add(audioContent, "file", "audio" + request.MediaType.GetAudioExtension());

        // REQUIRED diarization fields
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent("diarized_json"), "response_format");
        form.Add(new StringContent("auto"), "chunking_strategy");

        if (!string.IsNullOrEmpty(metadata?.Language))
        {
            form.Add(new StringContent("language"), metadata.Language);
        }

        var names = metadata?.KnownSpeakerNames?.ToList();
        var refs = metadata?.KnownSpeakerReferences?.ToList();

        // case 1: names + references
        if (names?.Count > 0 && refs?.Count > 0)
        {
            if (names.Count != refs.Count)
                throw new InvalidOperationException(
                    "known_speaker_names and known_speaker_references must have equal length"
                );

            for (var i = 0; i < names.Count; i++)
            {
                form.Add(new StringContent(names[i]), "known_speaker_names[]");
                form.Add(new StringContent(refs[i]), "known_speaker_references[]");
            }
        }
        // case 2: only names (NO references)
        else if (names?.Count > 0)
        {
            foreach (var name in names)
                form.Add(new StringContent(name), "known_speaker_names[]");
        }

        using var resp = await http.PostAsync(
            "https://api.openai.com/v1/audio/transcriptions",
            form,
            ct);

        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(json);

        return ConvertDiarizedJson(json, request.Model);
    }

    private static TranscriptionResponse ConvertDiarizedJson(
        string json,
        string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segmentsEl = root.TryGetProperty("segments", out var s)
            ? s
            : default;

        var segments = segmentsEl.ValueKind == JsonValueKind.Array
            ? segmentsEl.EnumerateArray()
                .Select(seg =>
                {
                    var text = seg.GetProperty("text").GetString() ?? "";

                    if (seg.TryGetProperty("speaker", out var speakerEl) &&
                        speakerEl.ValueKind == JsonValueKind.String)
                    {
                        var speaker = speakerEl.GetString();
                        if (!string.IsNullOrWhiteSpace(speaker))
                            text = $"{speaker}: {text}";
                    }

                    return new TranscriptionSegment
                    {
                        Text = text,
                        StartSecond = (float)seg.GetProperty("start").GetDouble(),
                        EndSecond = (float)seg.GetProperty("end").GetDouble()
                    };
                })
                .ToList()
            : [];


        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),

            Segments = segments,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Model == "gpt-4o-transcribe-diarize")
            return await TranscribeWithDiarization(request, cancellationToken);

        if (request.Model.EndsWith("/translate"))
            return await TranslateRequest(request, cancellationToken);

        var audioClient = new AudioClient(
            request.Model,
            GetKey()
        );

        var now = DateTime.UtcNow;
        List<string> results = [];
        List<object> warnings = [];
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var memStream = new MemoryStream(bytes, writable: false);
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());
        var options = new AudioTranscriptionOptions();

        if (!string.IsNullOrEmpty(metadata?.Language))
        {
            options.Language = metadata?.Language;
        }

        if (!string.IsNullOrEmpty(metadata?.Prompt))
        {
            options.Prompt = metadata?.Prompt;
        }

        options.Temperature = metadata?.Temperature;

        if (metadata?.TimestampGranularities?.Any() == true)
        {
            options.TimestampGranularities = (metadata.TimestampGranularities.Contains("word")
                                && metadata.TimestampGranularities.Contains("segment"))
                                ? AudioTimestampGranularities.Word | AudioTimestampGranularities.Segment
                                : metadata.TimestampGranularities.Contains("word")
                                    ? AudioTimestampGranularities.Word
                                    : metadata.TimestampGranularities.Contains("segment")
                                        ? AudioTimestampGranularities.Segment
                                        : default;
            options.ResponseFormat = AudioTranscriptionFormat.Verbose;
        }

        var result = await audioClient.TranscribeAudioAsync(memStream,
            "audio" + request.MediaType.GetAudioExtension(),
            options,
            cancellationToken);

        return new TranscriptionResponse()
        {
            Text = result.Value.Text,
            Segments = result.Value.Segments.Select(a => new TranscriptionSegment()
            {
                Text = a.Text,
                StartSecond = (float)a.StartTime.TotalSeconds,
                EndSecond = (float)a.EndTime.TotalSeconds
            }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = result.GetRawResponse().Content.ToString(),
            },
            Language = result.Value.Language,
            DurationInSeconds = result.Value.Duration.HasValue
                ? (float)result.Value.Duration.Value.TotalSeconds : null
        };
    }

    private async Task<TranscriptionResponse> TranslateRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var audioClient = new AudioClient(
            request.Model.Split("/").First(),
            GetKey()
        );

        var now = DateTime.UtcNow;
        List<string> results = [];
        List<object> warnings = [];
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        using var memStream = new MemoryStream(bytes, writable: false);
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());
        var options = new AudioTranslationOptions();

        if (!string.IsNullOrEmpty(metadata?.Prompt))
        {
            options.Prompt = metadata?.Prompt;
        }

        options.Temperature = metadata?.Temperature;
        options.ResponseFormat = AudioTranslationFormat.Verbose;

        var result = await audioClient.TranslateAudioAsync(memStream,
            "audio" + request.MediaType.GetAudioExtension(),
            options,
            cancellationToken);

        return new TranscriptionResponse()
        {
            Text = result.Value.Text,
            Segments = result.Value.Segments.Select(a => new TranscriptionSegment()
            {
                Text = a.Text,
                StartSecond = (float)a.StartTime.TotalSeconds,
                EndSecond = (float)a.EndTime.TotalSeconds
            }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = result.GetRawResponse().Content.ToString(),
            },
            Language = result.Value.Language,
            DurationInSeconds = result.Value.Duration.HasValue
                ? (float)result.Value.Duration.Value.TotalSeconds : null
        };
    }
}
