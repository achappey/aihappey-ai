using System.ComponentModel;
using System.Text.Json;
using AIHappey.Core.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.MCP.Media;

[McpServerToolType]
public class SpeechTools
{
    [Description("Generate speech audio using the unified speech endpoint.")]
    [McpServerTool(
        Title = "Generate speech",
        Name = "ai_speech_generate",
        Idempotent = false,
        ReadOnly = false,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AI_SpeechGenerate(
        [Description("AI model identifier")] string model,
        [Description("Text to turn into speech")] string text,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        [Description("Voice identifier (provider/model specific)")] string? voice = null,
        [Description("Audio output format (e.g. mp3, wav, ogg, opus, flac, aac, m4a)")] string? outputFormat = null,
        [Description("Optional speaking instructions/style")]
        string? instructions = null,
        [Description("Playback speed multiplier (provider/model specific)")] float? speed = null,
        [Description("Language code (e.g. en-US)")] string? language = null,
        [Description("Provider options as JSON object string. Example: {\"stability\":{...}}")]
        string? providerOptionsJson = null,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var request = new SpeechRequest
            {
                Model = model,
                Text = text,
                Voice = voice,
                OutputFormat = outputFormat,
                Instructions = instructions,
                Speed = speed,
                Language = language,
                ProviderOptions = ParseProviderOptions(providerOptionsJson)
            };

            if (string.IsNullOrWhiteSpace(request.Model))
                throw new ArgumentException("'model' is required.");
            if (string.IsNullOrWhiteSpace(request.Text))
                throw new ArgumentException("'text' is required.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var provider = await resolver.Resolve(request.Model, ct);
            request.Model = request.Model.SplitModelId().Model;

            var result = await provider.SpeechRequest(request, ct);

            if (string.IsNullOrWhiteSpace(result.Audio?.Base64))
                throw new InvalidOperationException("Provider returned no audio.");

            // Some providers may omit mimetype; use request/outputFormat as a best-effort fallback.
            if (string.IsNullOrWhiteSpace(result.Audio.MimeType))
                result.Audio.MimeType = GuessSpeechMimeType(request);

            return new CallToolResult
            {
                Content =
                [
                    new AudioContentBlock
                    {
                        MimeType = result.Audio.MimeType,
                        Data = Convert.FromBase64String(result.Audio.Base64)
                    }
                ],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    modelId = result.Response?.ModelId,
                    timestamp = result.Response?.Timestamp,
                    warnings = result.Warnings,
                    providerMetadata = result.ProviderMetadata
                }, JsonSerializerOptions.Web)
            };
        });

    private static Dictionary<string, JsonElement>? ParseProviderOptions(string? providerOptionsJson)
    {
        if (string.IsNullOrWhiteSpace(providerOptionsJson))
            return null;

        try
        {
            var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(providerOptionsJson, JsonSerializerOptions.Web);
            if (obj is null)
                throw new ArgumentException("'providerOptionsJson' must be a JSON object string.");
            return obj;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("'providerOptionsJson' must be valid JSON.", ex);
        }
    }

    private static string GuessSpeechMimeType(SpeechRequest request)
    {
        var fmt = request.OutputFormat?.Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" or "mpeg" or null or "" => "audio/mpeg",
            "wav" or "wave" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "m4a" or "mp4" => "audio/mp4",
            "webm" => "audio/webm",
            _ => "audio/mpeg"
        };
    }
}

