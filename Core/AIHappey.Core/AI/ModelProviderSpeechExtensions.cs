using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Responses;

namespace AIHappey.Core.AI;

public static class ModelProviderSpeechExtensions
{
    public static async IAsyncEnumerable<UIMessagePart> StreamSpeechAsync(
        this IModelProvider modelProvider,
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = string.Join("\n", chatRequest.Messages?
            .LastOrDefault(m => m.Role == Role.user)
            ?.Parts?.OfType<TextUIPart>().Select(a => a.Text) ?? []);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        var speechRequest = new SpeechRequest
        {
            Model = chatRequest.Model,
            Text = prompt,
            ProviderOptions = chatRequest.ProviderMetadata,
        };

        SpeechResponse? result = null;
        string? exceptionMessage = null;

        try
        {
            result = await modelProvider.SpeechRequest(speechRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.Message;
        }

        if (!string.IsNullOrEmpty(exceptionMessage))
        {
            yield return exceptionMessage.ToErrorUIPart();
            yield break;
        }

        //var audio = result?.Audio as string;
        if (string.IsNullOrWhiteSpace(result?.Audio?.Base64))
        {
            yield return "Provider returned no audio.".ToErrorUIPart();
            yield break;
        }

        var mimeType = "audio/mpeg";
        var base64 = result.Audio.Base64;

        yield return new FileUIPart
        {
            MediaType = mimeType,
            Url = base64.ToDataUrl(mimeType)
        };

        // Finish
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, null);
    }


    public static async Task<ResponseResult> SpeechResponseAsync(
       this IModelProvider modelProvider,
       ResponseRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        var input = chatRequest.Input?.IsText == true ?
            chatRequest.Input.Text : chatRequest.Input?.Items?
            .OfType<ResponseInputMessage>()
            .LastOrDefault()?.Content.Text;

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new Exception("No prompt provided.");
        }

        var speechRequest = new SpeechRequest
        {
            Model = chatRequest.Model!,
            Text = input,
            //      ProviderOptions = chatRequest.Metadata,
        };

        SpeechResponse? result;
        try
        {
            result = await modelProvider.SpeechRequest(speechRequest, cancellationToken);
        }
        catch (Exception e)
        {
            return new ResponseResult()
            {
                Id = Guid.NewGuid().ToString(),
                Error = new ResponseResultError()
                {
                    Code = "500",
                    Message = e.Message
                }
            };
        }

        if (result == null)
        {
            return new ResponseResult()
            {
                Id = Guid.NewGuid().ToString(),
                Error = new ResponseResultError()
                {
                    Code = "500",
                    Message = "No response"
                }
            };

        }

        //var audio = result?.Audio as string;
        if (string.IsNullOrWhiteSpace(result?.Audio?.Base64))
        {
            return new ResponseResult()
            {
                Id = Guid.NewGuid().ToString(),
                Error = new ResponseResultError()
                {
                    Code = "500",
                    Message = "No audio"
                }
            };
        }

        return new ResponseResult()
        {
            Id = Guid.NewGuid().ToString(),
            Model = result.Response.ModelId,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CreatedAt = new DateTimeOffset(result.Response.Timestamp)
                .ToUnixTimeSeconds(),
            Output = [
                new{
                type = "message",
                id = Guid.NewGuid().ToString(),
                status =  "completed",
                role = "assistant",
                content = new[] {
                    new {
                        type = "output_text",
                        text = JsonSerializer.Serialize(result, JsonSerializerOptions.Web),
                        annotations = Array.Empty<string>()
                    }
                }
            }]
        };
    }
}

