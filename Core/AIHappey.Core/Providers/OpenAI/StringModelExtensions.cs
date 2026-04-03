using OpenAI.Responses;

namespace AIHappey.Core.Providers.OpenAI;

public static class StringModelExtensions
{

    public static ResponseServiceTier ToResponseServiceTier(this string tier) => tier switch
    {
        "auto" => ResponseServiceTier.Auto,
        "default" => ResponseServiceTier.Default,
        "flex" => ResponseServiceTier.Flex,
        "scale" => ResponseServiceTier.Scale,
        "priority" => new ResponseServiceTier("priority"),
        _ => ResponseServiceTier.Auto
    };

    public static ResponseToolChoice ToResponseToolChoice(this string toolChoice) => toolChoice switch
    {
        "auto" => ResponseToolChoice.CreateAutoChoice(),
        "none" => ResponseToolChoice.CreateNoneChoice(),
        "required" => ResponseToolChoice.CreateRequiredChoice(),
        _ => ResponseToolChoice.CreateAutoChoice() 
    };
}