using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
        SpeechRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
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

            var structured = new JsonObject
            {
                ["modelId"] = result.Response?.ModelId,
                ["timestamp"] = result.Response?.Timestamp,
                ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, JsonSerializerOptions.Web),
                ["providerMetadata"] = JsonSerializer.SerializeToNode(result.ProviderMetadata, JsonSerializerOptions.Web)
            };

            return new CallToolResult
            {
                Content = [new AudioContentBlock { MimeType = result.Audio.MimeType,
                    Data = result.Audio.Base64 }],
                StructuredContent = structured
            };
        });

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

