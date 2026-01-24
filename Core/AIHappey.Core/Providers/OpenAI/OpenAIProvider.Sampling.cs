using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using Microsoft.AspNetCore.StaticFiles;
using ModelContextProtocol.Protocol;
using OpenAI.Responses;
using OAI = OpenAI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();

        if (model?.Contains("image") == true)
        {
            return await this.ImageSamplingAsync(chatRequest,
                    cancellationToken: cancellationToken);
        }

        if (model?.Contains("tts") == true)
        {
            return await this.SpeechSamplingAsync(chatRequest,
                    cancellationToken: cancellationToken);
        }

        if (model?.Contains("search-preview") == true)
        {
            return await ChatCompletionsSamplingAsync(chatRequest, cancellationToken);
        }

        return await ResponseSamplingAsync(chatRequest, cancellationToken);
    }

    public async Task<CreateMessageResult> ChatCompletionsSamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();
        var client = new OAI.OpenAIClient(
            GetKey()
        ).GetChatClient(model);

        IEnumerable<OAI.Chat.ChatMessage> inputItems = chatRequest.Messages.ToChatMessages();
        var clientResult = await client.CompleteChatAsync(inputItems, ToChatCompletionOptions(model!), cancellationToken);

        return new CreateMessageResult()
        {
            Model = clientResult.Value.Model,
            StopReason = clientResult.Value.FinishReason.ToStopReason(),
            Content = [clientResult.Value.Content.FirstOrDefault()?.Text?.ToTextContentBlock()!],
            Role = Role.Assistant
        };
    }

    private async Task<CreateMessageResult> ResponseSamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();
        var responseClient = new ResponsesClient(
            model,
            GetKey()
        );

        IEnumerable<ResponseItem> inputItems = chatRequest.Messages.ToResponseItems();
        var options = chatRequest.ToResponseCreationOptions();
        var searchTool = chatRequest.Metadata.ToWebSearchTool();

        if (searchTool != null)
        {
            options.Tools.Add(searchTool);
        }

        var fileTool = chatRequest.Metadata.ToFileSearchTool();
        if (fileTool != null)
        {
            options.Tools.Add(fileTool);
        }

        var codeInterpreterTool = chatRequest.Metadata.ToCodeInterpreterTool();
        if (codeInterpreterTool != null)
        {
            options.Tools.Add(codeInterpreterTool);
        }

        var mcpTools = chatRequest.Metadata.ToMcpTools();

        foreach (var tool in mcpTools ?? [])
        {
            options.Tools.Add(tool);
        }

        foreach (var i in inputItems)
        {
            options.InputItems.Add(i);
        }

        var clientResult = await responseClient
            .CreateResponseAsync(options, cancellationToken);

        var response = clientResult.Value;

        string? containerId = null;
        var fileIds = new List<string>();

        foreach (var item in response.OutputItems)
        {
            if (item is CodeInterpreterCallResponseItem codeInterpreterCallResponseItem)
            {
                containerId = codeInterpreterCallResponseItem.ContainerId;
            }

            if (item is MessageResponseItem msg && msg.Content is not null)
            {
                foreach (var part in msg.Content)
                {
                    var text = ((dynamic)part).Text as string ?? ((dynamic)part).InternalText as string;

                    // elk content-part kan annotations hebben
                    foreach (var ann in part.OutputTextAnnotations)
                    {
                        //TODO GET fileids

                    }
                }
            }
        }

        // distinct/cleanup
        fileIds = [.. fileIds.Distinct()];
        List<ContentBlock> contentBlocks = [response.GetOutputText().ToTextContentBlock()];

        if (fileIds.Count != 0 && !string.IsNullOrEmpty(containerId))
        {
            var containerClient = GetContainerClient();
            var provider = new FileExtensionContentTypeProvider();

            foreach (var fileId in fileIds)
            {
                var file = await containerClient.GetContainerFileAsync(containerId, fileId, cancellationToken);
                var fileContant = await containerClient.DownloadContainerFileAsync(containerId, fileId, cancellationToken);

                if (!provider.TryGetContentType(Path.GetFileName(file.Value.Path), out var contentType))
                {
                    // default/fallback
                    contentType = "application/octet-stream";
                }

                contentBlocks.Add(new EmbeddedResourceBlock()
                {
                    Resource = new BlobResourceContents()
                    {
                        Uri = $"https://api.openai.com/v1/containers/{containerId}/files/{fileId}",
                        MimeType = contentType,
                        Blob = Convert.ToBase64String(fileContant.Value.ToArray())
                    }
                });
            }
        }

        var meta = new JsonObject
        {
            ["inputTokens"] = clientResult.Value.Usage.InputTokenCount,
            ["totalTokens"] = clientResult.Value.Usage.TotalTokenCount
        };

        if (!string.IsNullOrEmpty(containerId))
            meta["containerId"] = containerId;

        ContentBlock resultBlock = contentBlocks.OfType<EmbeddedResourceBlock>().Any() ?
                 contentBlocks.OfType<EmbeddedResourceBlock>().First()
                 : string.Join(Environment.NewLine, contentBlocks.OfType<TextContentBlock>().Select(a => a.Text)).ToTextContentBlock()
                 ?? throw new Exception("No content");

        return new CreateMessageResult()
        {
            Model = clientResult.Value.Model,
            StopReason = "unknown",
            Content = [resultBlock],
            Role = Role.Assistant,
            Meta = meta
        };
    }
}
