using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Interfaze;

public partial class InterfazeProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        ArgumentNullException.ThrowIfNull(request.Audio);

        var instruction = "Transcribe the audio file.";
        var warnings = new List<object>();
        float? temperature = null;
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            foreach (var option in options.EnumerateObject())
            {
                switch (option.Name)
                {
                    case "language":
                        instruction += $" The spoken language is {option.Value.GetString()}.";
                        break;
                    case "prompt":
                        instruction += $" Additional transcription instructions: {option.Value.GetString()}";
                        break;
                    case "temperature":
                        temperature = option.Value.GetSingle();
                        break;
                    case "timestamp_granularities":
                        if (option.Value.EnumerateArray().Any(value => value.GetString() == "word"))
                            instruction += " Transcribe by word, with a timestamp for every word.";
                        break;
                    default:
                        warnings.Add(new { type = "unsupported", feature = option.Name,
                            reason = "Interfaze speech_to_text does not expose this transcription option." });
                        break;
                }
            }
        }

        var mediaType = request.MediaType;
        var extension = mediaType.Split('/').Last() == "mpeg" ? "mp3" : mediaType.Split('/').Last();
        var chatOptions = new ChatCompletionOptions
        {
            Model = request.Model.StartsWith("interfaze/", StringComparison.OrdinalIgnoreCase)
                ? request.Model["interfaze/".Length..] : request.Model,
            Stream = false,
            Temperature = temperature,
            Messages =
            [
                new ChatMessage
                {
                    Role = "system",
                    Content = JsonSerializer.SerializeToElement("<task>speech_to_text</task>")
                },
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = instruction },
                        CreateAudioFilePart(request.Audio.ToString()!, mediaType, "audio." + extension)
                    })
                }
            ]
        };

        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;
        var response = await this.GetChatCompletion(_client, chatOptions, cancellationToken: cancellationToken);
        var root = JsonSerializer.SerializeToElement(response, JsonSerializerOptions.Web);
        JsonElement? result = null;
        foreach (var choice in root.GetProperty("choices").EnumerateArray())
        {
            var content = choice.GetProperty("message").GetProperty("content");
            if (content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
                continue;
            using var task = JsonDocument.Parse(content.GetString()!);
            if (task.RootElement.GetProperty("name").GetString() != "speech_to_text")
                throw new JsonException("Interfaze returned an unexpected transcription task.");
            result = task.RootElement.GetProperty("result").Clone();
            break;
        }
        if (result is null)
            throw new JsonException("Interfaze did not return a speech_to_text result.");

        var text = result.Value.GetProperty("text").GetString()
            ?? throw new JsonException("Interfaze returned null transcription text.");
        var segments = new List<TranscriptionSegment>();
        if (result.Value.TryGetProperty("chunks", out var chunks))
        {
            foreach (var chunk in chunks.EnumerateArray())
            {
                var times = chunk.GetProperty("timestamp");
                // Preserve incomplete chunks in raw metadata rather than inventing timestamps.
                if (times.GetArrayLength() != 2 || times[0].ValueKind == JsonValueKind.Null
                    || times[1].ValueKind == JsonValueKind.Null)
                {
                    warnings.Add(new { type = "other", message = "A transcription chunk has incomplete timestamps; see provider metadata." });
                    continue;
                }
                segments.Add(new TranscriptionSegment
                {
                    Text = chunk.GetProperty("text").GetString()!,
                    StartSecond = times[0].GetSingle(),
                    EndSecond = times[1].GetSingle()
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Segments = segments,
            Warnings = warnings,
            Language = result.Value.TryGetProperty("language", out var language) ? language.GetString() : null,
            ProviderMetadata = new() { [GetIdentifier()] = root },
            Response = new ResponseData { Timestamp = timestamp, ModelId = response.Model, Body = root },
            Request = new TranscriptionRequestItem { Body = JsonSerializer.Serialize(chatOptions, JsonSerializerOptions.Web) }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        var result = response.ToOpenAITranscriptionResponse(options.ResolveOpenAITranscriptionResponseFormat());
        if (result is OpenAITranscriptionVerboseResponse verbose
            && options.TimestampGranularities?.Contains("word") == true)
        {
            verbose.Words = response.Segments.Select(segment => new OpenAITranscriptionWord
            {
                Word = segment.Text, Start = segment.StartSecond, End = segment.EndSecond
            }).ToArray();
        }
        return result;
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Text.Length > 0)
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InputAudioFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputAudioFormat.Type);
        var response = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = request.InputAudioFormat.Type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                ? request.InputAudioFormat.Type : AudioMediaType(request.InputAudioFormat.Type),
            ProviderOptions = request.ProviderOptions
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new TranscriptionStreamStartPart { Warnings = response.Warnings };
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = response.Response.Timestamp, ModelId = response.Response.ModelId, Body = response.Response.Body
        };
        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = response.Response.Body };
        if (response.Text.Length > 0)
            yield return new TranscriptionDeltaPart { Delta = response.Text };
        yield return new TranscriptionFinalPart { Text = response.Text, ProviderMetadata = response.ProviderMetadata };
        yield return new TranscriptionFinishPart
        {
            Text = response.Text, Segments = response.Segments, Language = response.Language,
            ProviderMetadata = response.ProviderMetadata
        };
    }
}
