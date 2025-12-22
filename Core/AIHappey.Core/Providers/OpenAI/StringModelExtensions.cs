using OpenAI.Responses;

namespace AIHappey.Core.Providers.OpenAI;

public static class StringModelExtensions
{

    public static ResponseToolChoice ToResponseToolChoice(this string toolChoice) => toolChoice switch
    {
        "auto" => ResponseToolChoice.CreateAutoChoice(),
        "none" => ResponseToolChoice.CreateNoneChoice(),
        "required" => ResponseToolChoice.CreateRequiredChoice(),
        // add more as needed
        _ => ResponseToolChoice.CreateAutoChoice() // or throw, or some fallback
    };
}