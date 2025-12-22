using AIHappey.Core.AI;
using AIHappey.Common.Model;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{
    public static async Task<FileUIPart> GetFileUIPart(this ANT.AnthropicClient anthropicClient, string fileId, CancellationToken cancellationToken)
    {
        var fileItem = await anthropicClient.Files.GetFileMetadataAsync(fileId, cancellationToken: cancellationToken);
        var fileDownload = await anthropicClient.Files.DownloadFileAsync(fileId, ctx: cancellationToken);

        return fileDownload.ToFileUIPart(fileItem.MimeType);
    }
}
