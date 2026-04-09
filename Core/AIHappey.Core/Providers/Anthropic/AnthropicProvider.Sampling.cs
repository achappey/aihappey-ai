using ModelContextProtocol.Protocol;
using AIHappey.Sampling.Mapping;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
          cancellationToken);

        return result.ToSamplingResult();

       /* var betaHeaders = chatRequest.Metadata.ToAnthropicBetaFeatures();
        AddBetaHeaders(betaHeaders);

        var responseClient = new ANT.AnthropicClient(GetKey());

        var clientResult = await responseClient.Messages
           .GetClaudeMessageAsync(chatRequest
               .ToMessageParameters([]),
               ctx: cancellationToken);

        IEnumerable<string> result = clientResult.Content
            .OfType<TextContent>()
            .Select(a =>
            {
                var text = a.Text ?? string.Empty;
                if (a.Citations == null || !a.Citations.Any())
                    return text;

                var citations = string.Join(
                    Environment.NewLine,
                    a.Citations.Select(z =>
                        !string.IsNullOrWhiteSpace(z.Title) && !string.IsNullOrWhiteSpace(z.Url)
                            ? $"- [{z.Title}]({z.Url})"
                            : $"- {z.CitedText}") // fallback, voor als er geen titel/uri is
                );

                return $"{text}\n\nSources:\n{citations}";
            }) ?? [];

        List<EmbeddedResourceBlock> embeddedResourceBlocks = [];

        foreach (var content in clientResult.Content ?? [])
        {
            if (content is BashCodeExecutionToolResultContent bashResult)
            {
                if (bashResult.Content is BashCodeExecutionResultContent output)
                {
                    foreach (var outputContent in output.Content?.Where(a => !string.IsNullOrEmpty(a.FileId)) ?? [])
                    {
                        var fileItem = await responseClient.Files.GetFileMetadataAsync(outputContent.FileId, cancellationToken: cancellationToken);
                        var fileDownload = await responseClient.Files.DownloadFileAsync(outputContent.FileId, ctx: cancellationToken);

                        embeddedResourceBlocks.Add(new EmbeddedResourceBlock()
                        {
                            Resource = new BlobResourceContents()
                            {
                                Uri = "https://api.anthropic.com/v1/files/" + outputContent.FileId,
                                MimeType = fileItem.MimeType,
                                Blob = fileDownload
                            }
                        });
                    }
                }
            }
        }

        var meta = new JsonObject
        {
            ["inputTokens"] = clientResult.Usage?.InputTokens,
            ["totalTokens"] = clientResult.Usage?.InputTokens + clientResult.Usage?.OutputTokens
        };

        if (!string.IsNullOrEmpty(clientResult.Container?.Id))
            meta["containerId"] = clientResult.Container.Id;

        List<ModelContextProtocol.Protocol.ContentBlock> blocks =
        [
            .. embeddedResourceBlocks,
            .. result.Select(a => a.ToTextContentBlock())
        ];

        ModelContextProtocol.Protocol.ContentBlock resultBlock = blocks.OfType<EmbeddedResourceBlock>().Any() ?
                 blocks.OfType<EmbeddedResourceBlock>().First()
                 : string.Join(Environment.NewLine, blocks.OfType<TextContentBlock>().Select(a => a.Text)).ToTextContentBlock()
                 ?? throw new Exception("No content");

        return new CreateMessageResult
        {
            Model = clientResult.Model,
            StopReason = clientResult.StopReason,
            Content = [resultBlock],
            Role = Role.Assistant,
            Meta = meta
        };*/
    }
}