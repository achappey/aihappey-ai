using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Extensions;

public static class RequestExtensions
{
    public static void AppendOrAddHeader(
    this Dictionary<string, string> headers,
    string key,
    string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (headers.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            headers[key] = $"{existing},{value}";
        }
        else
        {
            headers[key] = value;
        }
    }

    public static Dictionary<string, Dictionary<string, object>> ToProviderMetadata(
           this Dictionary<string, object> metadata, string id)
        => new()
        { { id, metadata } };

    public static ChatRequest ToChatRequest(
            this UIRequest uIRequest)
    => new()
    {
        ProviderMetadata = uIRequest.ProviderMetadata,
        Model = uIRequest.Model.SplitModelId().Model,
        MaxOutputTokens = uIRequest.MaxOutputTokens,
        Temperature = uIRequest.Temperature,
        Messages =
                          [
                              new()
                {
                    Role = Role.system,
                    Parts =
                    [
                        new TextUIPart()
                        {
                            Text = JsonSerializer.Serialize(
                               uIRequest.Context ?? new {},
                             JsonSerializerOptions.Web)
                        },
                        new TextUIPart()
                        {
                            Text = uIRequest.CatalogPrompt
                        }
                    ]
                },
                new()
                {
                    Role = Role.user,
                    Parts =
                    [
                        new TextUIPart()
                        {
                            Text = uIRequest.Prompt
                        }
                    ]
                }
            ]
    };

    public static string ToDataUrl(
        this BinaryData data, string mimeType) => $"data:{mimeType};base64,{Convert.ToBase64String(data)}";

    public static ImageFile ToImageFile(
        this FileUIPart data) => new()
        {
            Data = data.Url.RemoveDataUrlPrefix(),
            MediaType = data.MediaType
        };

    public static string ToDataUrl(this ImageFile imageContentBlock)
    {
        ArgumentNullException.ThrowIfNull(imageContentBlock);
        if (imageContentBlock.Data is null)
            throw new ArgumentException("Image input must include inline data.", nameof(imageContentBlock));
        if (imageContentBlock.MediaType is null)
            throw new ArgumentException("Image input must include a media type.", nameof(imageContentBlock));

        return imageContentBlock.Data.ToDataUrl(imageContentBlock.MediaType);
    }

}
