using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderSpeechExtensions
{
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
