using System.Runtime.CompilerServices;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        var emittedImageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;

            await foreach (var filePart in DownloadImageFilePartsAsync(part, emittedImageUrls, cancellationToken))
                yield return filePart;
        }
    }

    private async IAsyncEnumerable<FileUIPart> DownloadImageFilePartsAsync(
        AIHappey.Unified.Models.AIStreamEvent streamEvent,
        HashSet<string> emittedImageUrls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in ExtractExaResults(streamEvent.Metadata))
        {
            var imageUrl = TryGetString(result, "image");
            if (string.IsNullOrWhiteSpace(imageUrl) || !emittedImageUrls.Add(imageUrl!))
                continue;

            var filePart = await DownloadResultImageFilePartAsync(result, imageUrl!, cancellationToken);
            if (filePart is not null)
                yield return filePart;
        }
    }
}
