using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.MCP.Media;

[McpServerToolType]
public class TranscriptionTools
{
    [Description("Create an audio transcription using the unified Vercel-compatible transcription endpoint. IMPORTANT: This tool accepts only publicly accessible http(s) URLs (no base64 input). The server will download the audio internally and convert it to the provider-required payload.")]
    [McpServerTool(
        Title = "Create transcription",
        Name = "ai_audio_transcriptions_create",
        Idempotent = false,
        ReadOnly = false,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AI_AudioTranscriptionsCreate(
        [Description("AI model identifier")] string model,
        [Description("Publicly accessible http(s) URL to the audio file. Only public URLs work.")] string audioUrl,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("'model' is required.");
            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new ArgumentException("'audioUrl' is required.");

            if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri))
                throw new ArgumentException("'audioUrl' must be an absolute URL.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

            var (mimeType, base64) = await MediaContentHelpers.FetchExternalAudioAsBase64Async(uri, httpClientFactory, ct);

            var request = new TranscriptionRequest
            {
                Model = model,
                // Providers typically expect raw base64 or data-url; we standardize on raw base64.
                Audio = base64,
                MediaType = mimeType,
                ProviderOptions = null
            };

            var provider = await resolver.Resolve(request.Model, ct);
            request.Model = request.Model.SplitModelId().Model;

            var result = await provider.TranscriptionRequest(request, ct);
            if (string.IsNullOrWhiteSpace(result.Text))
                throw new InvalidOperationException("Provider returned no transcription text.");

            var structured = new JsonObject
            {
                // full contract response as structured content
                ["providerMetadata"] = JsonSerializer.SerializeToNode(result.ProviderMetadata, JsonSerializerOptions.Web),
                ["text"] = result.Text,
                ["language"] = result.Language,
                ["durationInSeconds"] = result.DurationInSeconds,
                ["warnings"] = JsonSerializer.SerializeToNode(result.Warnings, JsonSerializerOptions.Web),
                ["segments"] = JsonSerializer.SerializeToNode(result.Segments, JsonSerializerOptions.Web),
                ["response"] = JsonSerializer.SerializeToNode(result.Response, JsonSerializerOptions.Web)
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = result.Text }
                ],
                StructuredContent = structured
            };
        });
}

