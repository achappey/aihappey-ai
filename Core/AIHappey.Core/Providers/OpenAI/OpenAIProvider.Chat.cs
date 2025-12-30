using OpenAI.Responses;
using AIHappey.Common.Model;
using OpenAI.Files;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Model.Providers;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = chatRequest.Model!;

        IEnumerable<ResponseItem> inputItems = chatRequest.Messages.SelectMany(a => a.ToResponseItems());

        var lastUser = chatRequest.Messages.LastOrDefault(a => a.Role == Role.user);
        var mimeSet = OpenAIModelExtensions.CodeInterpreterMimeTypes;
        var allFileParts = new List<FileUIPart>();

        foreach (var p in lastUser?.Parts ?? Enumerable.Empty<UIMessagePart>())
        {
            if (p is FileUIPart f && mimeSet.Contains(f.MediaType))
                allFileParts.Add(f);
        }

        var containerClient = GetContainerClient();

        List<string> codeInterpreterFiles = [];

        var metadata = chatRequest.GetProviderMetadata<OpenAiProviderMetadata>(GetIdentifier());

        if (allFileParts?.Count > 0
            && metadata?.CodeInterpreter != null
            && metadata.CodeInterpreter.Container != null)
        {
            if (metadata.CodeInterpreter.Container.Value.IsString)
            {
                foreach (var file in allFileParts)
                {
                    await containerClient.UploadDataUriAsync(
                        metadata.CodeInterpreter.Container.Value.String!,
                        file.Url, file.MediaType);
                }
            }
            else
            {
                var fileClient = GetFileClient();

                foreach (var file in allFileParts)
                {
                    string dataUrl = file.Url;

                    // find the comma that separates header from data
                    int commaIndex = dataUrl.IndexOf(',');
                    if (commaIndex < 0)
                        throw new FormatException("Invalid Data URI");

                    // get the base64 part only
                    string base64 = dataUrl.Substring(commaIndex + 1);

                    await using var ms = new MemoryStream(Convert.FromBase64String(base64));
                    var provider = new FileExtensionContentTypeProvider();
                    string extension = ".bin"; // fallback
                    var timestamp = DateTime.UtcNow.ToString("yyMMdd_HHmmss");
                    foreach (var kvp in provider.Mappings)
                    {
                        if (string.Equals(kvp.Value, file.MediaType, StringComparison.OrdinalIgnoreCase))
                        {
                            extension = kvp.Key;
                            break;
                        }
                    }

                    var filename = $"{timestamp}{extension}";
                    var result = await fileClient.UploadFileAsync(
                        ms,
                        filename,
                        FileUploadPurpose.UserData,
                        cancellationToken
                    );

                    codeInterpreterFiles.Add(result.Value.Id);
                }
            }
        }

        var options = chatRequest.ToResponseCreationOptions(codeInterpreterFiles);
        var responseClient = new ResponsesClient(
            model,
            GetKey()
        );

        foreach (var i in inputItems)
        {
            options.InputItems.Add(i);
        }

        var stream = responseClient.CreateResponseStreamingAsync(options, cancellationToken);

        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            await foreach (var responseUpdate in update.ToStreamingResponseUpdate(containerClient,
                chatRequest.ResponseFormat))
            {
                if (responseUpdate is ToolCallStreamingStartPart toolCallStreamingStartPart)
                    toolCallStreamingStartPart.Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolCallStreamingStartPart.ToolName)?.Title;

                if (responseUpdate is ToolCallPart toolCallPart)
                    toolCallPart.Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolCallPart.ToolName)?.Title;

                yield return responseUpdate;
            }
        }
    }
}
