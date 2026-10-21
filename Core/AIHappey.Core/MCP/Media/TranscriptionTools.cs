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
public class TranscriptionTools
{
    [Description("Create an audio transcription using the unified Vercel-compatible transcription endpoint. Audio URLs are passed directly only to providers that natively support URL input; other providers reject URL inputs.")]
    [McpServerTool(
        Title = "Create transcription",
        Name = "ai_audio_transcriptions_create",
        Idempotent = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(TranscriptionResponse),
        OpenWorld = true)]
    public static async Task<CallToolResult?> AI_AudioTranscriptionsCreate(
        [Description("AI model identifier")] string model,
        [Description("http(s) URL to the audio file. The provider must natively support URL input.")] string audioUrl,
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

            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("'audioUrl' must use http or https.");

            var resolver = services.GetRequiredService<IAIModelProviderResolver>();
            var provider = await resolver.Resolve(model, ct);
            var request = CreateUrlTranscriptionRequest(model, provider.GetIdentifier(), uri);
            request.Model = request.Model.SplitModelId().Model;

            var result = await provider.TranscriptionRequest(request, ct);
            if (string.IsNullOrWhiteSpace(result.Text))
                throw new InvalidOperationException("Provider returned no transcription text.");

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = result.Text }
                ],
                StructuredContent = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web)
            };
        });

    internal static TranscriptionRequest CreateUrlTranscriptionRequest(string model, string providerId, Uri uri)
    {
        var request = new TranscriptionRequest
        {
            Model = model,
            MediaType = "application/octet-stream"
        };

        switch (providerId)
        {
            case "deapi":
                request.Audio = uri.AbsoluteUri;
                break;
            case "spacexai":
                request.ProviderOptions = new Dictionary<string, JsonElement>
                {
                    [providerId] = JsonSerializer.SerializeToElement(new { url = uri.AbsoluteUri }, JsonSerializerOptions.Web)
                };
                break;
            case "token360":
                request.ProviderOptions = new Dictionary<string, JsonElement>
                {
                    [providerId] = JsonSerializer.SerializeToElement(new { file_url = uri.AbsoluteUri }, JsonSerializerOptions.Web)
                };
                break;
            default:
                throw new NotSupportedException($"Provider '{providerId}' does not natively support audio URL input.");
        }

        return request;
    }
}
