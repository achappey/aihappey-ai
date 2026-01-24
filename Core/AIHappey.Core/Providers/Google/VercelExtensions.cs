using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using Mscc.GenerativeAI;

namespace AIHappey.Core.Providers.Google;

public static class VercelExtensions
{
    public static Schema ToParameterSchema(
                 this ToolInputSchema a) => new()
                 {
                     Type = ParameterType.Object,
                     Required = a?.Required,
                     Properties = a?.Properties
                 };

    public static FunctionDeclaration ToFunctionDeclaration(
               this Common.Model.Tool a) => new()
               {
                   Name = a.Name,
                   Description = a.Description,
                   Parameters = a.InputSchema?.ToParameterSchema()
               };

    public static IEnumerable<FunctionDeclaration> ToFunctionDeclarations(
               this IEnumerable<Common.Model.Tool> data) =>
               data.Select(a => a.ToFunctionDeclaration());

    public static GenerationConfig ToGenerationConfig(this ChatRequest chatRequest, GoogleProviderMetadata? metadata) => new()
    {
        Temperature = chatRequest.Temperature,
        TopP = chatRequest.TopP,
        MediaResolution = metadata?.MediaResolution,
        ThinkingConfig = metadata?.ToThinkingConfig(chatRequest.Model),
        ResponseJsonSchema = chatRequest.ResponseFormat,
        ResponseModalities = [ResponseModality.Text],
        EnableEnhancedCivicAnswers = metadata?.EnableEnhancedCivicAnswers,
        Seed = metadata?.Seed
    };


    public static ThinkingConfig ToThinkingConfig(this GoogleProviderMetadata? chatRequest, string model) => new()
    {
        IncludeThoughts = chatRequest?.ThinkingConfig?.IncludeThoughts,
        ThinkingLevel = model.StartsWith("gemini-3")
            ? chatRequest?.ThinkingConfig?.ThinkingLevel
               : null,
        ThinkingBudget = !model.StartsWith("gemini-3")
                   ? chatRequest?.ThinkingConfig?.ThinkingBudget
               : null,
    };

    public static string ToRole(this Vercel.Models.Role role)
        => role == Vercel.Models.Role.assistant ? "model" : "user";

    public static ContentResponse ToContentResponse(this UIMessage message)
        => new(message.Parts
            .OfType<TextUIPart>()
            .FirstOrDefault()?.Text!, message.Role.ToRole())
        {
            Parts = [
                .. message.Parts
                .SelectMany(a => a.ToParts())
            ]
        };

    public static IEnumerable<Part> ToParts(this UIMessagePart message)
    {
        switch (message)
        {
            case TextUIPart textUIPart:
                yield return new(textUIPart.Text);
                break;

            case ReasoningUIPart thoughtPart:
                var signature = thoughtPart.ProviderMetadata.GetReasoningSignature(GoogleExtensions.Identifier());

                yield return new Part(thoughtPart.Text)
                {
                    Thought = true,
                    ThoughtSignature = !string.IsNullOrEmpty(signature) ?
                        Convert.FromBase64String(signature) : null
                };
                break;

            case ToolInvocationPart toolPart:
                if (toolPart.ProviderExecuted != true)
                {
                    var toolName = toolPart.GetToolName();

                    yield return new Part()
                    {
                        FunctionCall = new FunctionCall()
                        {
                            Name = toolName,
                            Args = toolPart.Input,
                            Id = toolPart.ToolCallId
                        }
                    };

                    yield return new Part()
                    {
                        FunctionResponse = new FunctionResponse()
                        {
                            Name = toolName,
                            Response = toolPart.Output,
                            Id = toolPart.ToolCallId
                        }
                    };
                }
                break;

            case FileUIPart fileUIPart:
                var commaIndex = fileUIPart.Url.IndexOf(',');

                var base64Data = commaIndex >= 0
                    ? fileUIPart.Url[(commaIndex + 1)..]
                    : fileUIPart.Url;

                yield return new Part()
                {
                    InlineData = Part.FromBytes(base64Data, fileUIPart.MediaType)
                };

                break;

            default:
                break;
        }
    }



}
