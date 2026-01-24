using ModelContextProtocol.Protocol;
using AIHappey.Core.AI;
using System.Net.Mime;
using System.Text;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();
        var googleAI = GetClient();

        List<Mscc.GenerativeAI.ContentResponse> inputItems = [.. chatRequest.Messages
            .SkipLast(1)
            .Select(a => a.ToContentResponse())];

        Mscc.GenerativeAI.GoogleSearch? googleSearch = chatRequest.Metadata.ToGoogleSearch();
        Mscc.GenerativeAI.CodeExecution? codeExecution = chatRequest.Metadata.UseCodeExecution() ? new() : null;
        Mscc.GenerativeAI.UrlContext? urlContext = chatRequest.Metadata.UseUrlContext() ? new() : null;
        Mscc.GenerativeAI.GoogleMaps? googleMaps = chatRequest.Metadata.UseGoogleMaps() ? new() : null;

        Mscc.GenerativeAI.Tool? tool = urlContext != null
            || googleSearch != null
            || googleMaps != null
            || codeExecution != null ? new()
            {
                GoogleSearch = googleSearch,
                UrlContext = urlContext,
                GoogleMaps = googleMaps,
                CodeExecution = codeExecution
            } : null;

        Mscc.GenerativeAI.ChatSession chat = googleAI.ToChatSession(chatRequest.ToGenerationConfig(), model!,
            chatRequest.SystemPrompt ?? string.Empty,
            inputItems);

        var text = chatRequest.Messages.LastOrDefault()?.ToText();

        List<Mscc.GenerativeAI.Part> parts = [new Mscc.GenerativeAI.Part() { Text =
            text }];

        var response = await chat.SendMessage(parts,
            tools: tool != null ? [tool] : null,
            cancellationToken: cancellationToken);

        List<ContentBlock> inlineDataBlocks = response.Candidates?.FirstOrDefault()?.Content?
            .Parts.Where(a => a.InlineData != null)?
            .Select(a => a.InlineData)
            .Select(a => a?.MimeType.StartsWith("image/") == true ? new ImageContentBlock()
            {
                MimeType = a?.MimeType!,
                Data = a?.Data!
            } : (ContentBlock)new EmbeddedResourceBlock()
            {
                Resource = a?.MimeType.StartsWith("text/") == true
                    || a?.MimeType.StartsWith(MediaTypeNames.Application.Json) == true
                    ? new TextResourceContents()
                    {
                        Text = Encoding.UTF8.GetString(Convert.FromBase64String(a?.Data!)),
                        MimeType = a?.MimeType,
                        Uri = FILES_API
                    } : new BlobResourceContents()
                    {
                        Blob = a?.Data!,
                        MimeType = a?.MimeType,
                        Uri = FILES_API
                    }
            })
            .ToList() ?? [];

        var textBlock = !string.IsNullOrEmpty(response.Text) ? response.Text?.ToTextContentBlock() : null;

        if (textBlock != null)
            inlineDataBlocks.Add(textBlock);

        ContentBlock resultBlock = inlineDataBlocks.OfType<EmbeddedResourceBlock>().Any() ?
                 inlineDataBlocks.OfType<EmbeddedResourceBlock>().First()
                 : string.Join(Environment.NewLine, inlineDataBlocks.OfType<TextContentBlock>().Select(a => a.Text)).ToTextContentBlock()
                 ?? throw new Exception("No content");

        return new CreateMessageResult()
        {
            Model = response.ModelVersion!,
            Content = [resultBlock],
            StopReason = response.Candidates?.FirstOrDefault()?.FinishReason.ToStopReason(),
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Meta = new System.Text.Json.Nodes.JsonObject()
            {
                ["inputTokens"] = response?.UsageMetadata?.PromptTokenCount,
                ["totalTokens"] = response?.UsageMetadata?.TotalTokenCount
            }
        };
    }

}
