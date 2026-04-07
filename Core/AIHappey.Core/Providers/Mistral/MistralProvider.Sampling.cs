using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var conversationTarget = ResolveConversationTarget(chatRequest.GetModel());
        try
        {
            var response = await StartConversationAsync(
                BuildSamplingConversationRequest(chatRequest, conversationTarget),
                cancellationToken);

            return await MapSamplingResponseAsync(response, conversationTarget, cancellationToken);
        }
        catch (MistralConversationException ex)
        {
            return new CreateMessageResult
            {
                Role = Role.Assistant,
                Model = conversationTarget.ExposedModelId,
                StopReason = "error",
                Content = [$"Mistral conversations error: {ex.Message}".ToTextContentBlock()]
            };
        }
    }

    private MistralConversationRequest BuildSamplingConversationRequest(
        CreateMessageRequestParams chatRequest,
        ConversationTarget conversationTarget)
    {
        var inputs = chatRequest.Messages
            .Select(message => new
            {
                type = "message.input",
                role = ToMistralSamplingRole(message.Role),
                content = message.Content
                    .Select(ToMistralSamplingContentPart)
                    .OfType<object>()
                    .ToArray()
            })
            .ToArray();

        var tools = new List<JsonNode>();

        if (chatRequest.Metadata.TryGetExplicitToolNodes(out var passthroughTools))
        {
            tools.AddRange(passthroughTools);
        }

        return CreateConversationRequest(
            conversationTarget,
            JsonSerializer.SerializeToNode(inputs, MistralJsonSerializerOptions) ?? new JsonArray(),
            chatRequest.SystemPrompt,
            new MistralConversationCompletionArgs
            {
                Temperature = chatRequest.Temperature,
                MaxTokens = chatRequest.MaxTokens
            },
            ToToolArrayNode(tools),
            stream: false);
    }

    private static string ToMistralSamplingRole(Role role)
        => role == Role.User ? "user" : "assistant";

    private static object? ToMistralSamplingContentPart(ContentBlock content)
        => content switch
        {
            TextContentBlock text => new { type = "text", text = text.Text },
            ImageContentBlock image => new { type = "image_url", image_url = image.ToDataUrl() },
            _ => null
        };

    private async Task<CreateMessageResult> MapSamplingResponseAsync(
        MistralConversationResponse response,
        ConversationTarget conversationTarget,
        CancellationToken cancellationToken)
    {
        var primaryOutput = GetPrimaryMessageOutput(response);
        var resolvedModel = NormalizeReportedModel(GetString(primaryOutput, "model"), conversationTarget);
        List<ContentBlock> contentBlocks = [];

        foreach (var part in EnumerateContentParts(primaryOutput?["content"]))
        {
            if (part.Type is "output_text" or "text")
            {
                if (!string.IsNullOrEmpty(part.Text))
                    contentBlocks.Add(part.Text.ToTextContentBlock());

                continue;
            }

            if (part.Type != "tool_file")
                continue;

            var download = await TryDownloadConversationFileAsync(part.FileId, part.FileType, cancellationToken);
            if (download.File is null)
                continue;

            contentBlocks.Add(new EmbeddedResourceBlock
            {
                Resource = new BlobResourceContents
                {
                    Uri = $"https://api.mistral.ai/v1/files/{download.File.FileId}",
                    MimeType = download.File.MimeType ?? MediaTypeNames.Application.Octet,
                    Blob = download.File.Bytes
                }
            });
        }

        var usage = ExtractUsage(response.Usage);
        var meta = new JsonObject
        {
            ["inputTokens"] = usage.PromptTokens,
            ["totalTokens"] = usage.TotalTokens
        };

        if (!string.IsNullOrWhiteSpace(response.ConversationId))
            meta["conversationId"] = response.ConversationId;

        ContentBlock resultBlock = contentBlocks.OfType<EmbeddedResourceBlock>().Any()
            ? contentBlocks.OfType<EmbeddedResourceBlock>().First()
            : string.Join(Environment.NewLine, contentBlocks.OfType<TextContentBlock>().Select(block => block.Text))
                .ToTextContentBlock();

        return new CreateMessageResult
        {
            Role = Role.Assistant,
            Model = resolvedModel,
            StopReason = "stop",
            Content = [resultBlock],
            Meta = meta
        };
    }
}
