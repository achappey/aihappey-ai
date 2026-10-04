using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider
{
    // generationType selects the endpoint; remaining options pass through unchanged.
    private async Task<SpeechResponse> GenerateAudioAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);

        if (request.Voice is not null || request.Speed is not null || request.Language is not null
            || request.Instructions is not null || request.OutputFormat is not null)
            throw new NotSupportedException("AppNZ music/SFX does not have verified mappings for speech voice, speed, language, instructions or outputFormat. Supply model-specific fields as raw AppNZ options.");

        var payload = GetMediaOptions(request.ProviderOptions);
        var kind = ReadOptionString(payload, "generationType");
        var endpoint = kind switch
        {
            "music" => "v1/music/generations",
            "sfx" => "v1/audio/generations",
            _ => throw new NotSupportedException("AppNZ audio generationType must be music or sfx.")
        };
        payload.Remove("generationType");
        payload["model"] = request.Model;
        payload["prompt"] = request.Text;

        using var client = CreateClient();
        var started = await SendJsonAsync(client, HttpMethod.Post, endpoint, payload, cancellationToken);
        var raw = started;
        var url = GetMediaUrl(raw, "audio");
        var id = GetJobId(raw);

        if (url is null && id is not null && GetJobStatus(raw) is not ("completed" or "succeeded" or "failed" or "expired"))
        {
            raw = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                ct => SendJsonAsync(client, HttpMethod.Get, endpoint + "/" + Uri.EscapeDataString(id), null, ct),
                result => GetMediaUrl(result, "audio") is not null
                    || GetJobStatus(result) is "completed" or "succeeded" or "failed" or "expired",
                interval: TimeSpan.FromMilliseconds(2500),
                timeout: TimeSpan.FromMinutes(30),
                maxAttempts: 720,
                cancellationToken: cancellationToken);
            url = GetMediaUrl(raw, "audio");
        }

        if (GetJobStatus(raw) is "failed" or "expired")
            throw new InvalidOperationException(GetMediaError(raw, "AppNZ audio generation failed."));
        if (url is null)
            throw new InvalidOperationException($"AppNZ audio generation returned no downloadable audio (or exceeded the public-client polling limit): {raw.GetRawText()}");

        var (bytes, mimeType) = await DownloadMediaAsync(url, "audio", cancellationToken);
        var format = mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/ogg" => "ogg",
            "audio/mp4" => "m4a",
            "audio/flac" => "flac",
            "audio/webm" => "webm",
            _ => throw new NotSupportedException($"AppNZ audio format '{mimeType}' cannot be represented by the speech adapter.")
        };
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { start = started.Clone(), raw = raw.Clone() }),
            Request = new SpeechRequestItem { Body = payload },
            Response = new ResponseData
            {
                ModelId = CreateMediaResponse(request.Model).ModelId,
                Timestamp = DateTime.UtcNow,
                Body = raw.Clone()
            }
        };
    }
}
