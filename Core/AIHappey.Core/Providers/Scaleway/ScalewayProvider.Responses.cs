using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var scalewayOptions = NormalizeScalewayResponseRequest(options);

        return await this.GetResponse(_client,
                   scalewayOptions,
                   cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var scalewayOptions = NormalizeScalewayResponseRequest(options);

        return this.GetResponses(_client,
           scalewayOptions,
           cancellationToken: cancellationToken);
    }

    private static ResponseRequest NormalizeScalewayResponseRequest(ResponseRequest options)
    {
        if (options.Input is not { IsItems: true, Items: not null })
            return options;

        options.Input = new ResponseInput(
            options.Input.Items.Select(MapScalewayInputItem));

        return options;
    }

    private static ResponseInputItem MapScalewayInputItem(ResponseInputItem item)
    {
        if (item is not ResponseInputMessage message)
            return item;

        if (message.Role != ResponseRole.Assistant)
            return item;

        return new ResponseInputMessage
        {
            Role = ResponseRole.Assistant,
            Content = MapScalewayAssistantContent(message.Content)
        };
    }

    private static ResponseMessageContent MapScalewayAssistantContent(ResponseMessageContent content)
    {
        if (content.IsText)
            return content;

        if (content.Parts is null)
            return content;

        return new ResponseMessageContent(
            content.Parts.Select(MapAssistantOutputPartToInputPart));
    }

    private static ResponseContentPart MapAssistantOutputPartToInputPart(ResponseContentPart part)
        => part switch
        {
            OutputTextPart output => new InputTextPart(output.Text),
            _ => part
        };

}
