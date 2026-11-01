using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Models;
using AIHappey.Decisions;
using AIHappey.Interactions;
using AIHappey.Interactions.Extensions;
using AIHappey.Interactions.Mapping;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Contracts;

public sealed class StandaloneLibraryDependencyTests
{
    [Theory]
    [InlineData(typeof(ChatRequest))]
    [InlineData(typeof(ResponseRequest))]
    [InlineData(typeof(MessagesRequest))]
    [InlineData(typeof(InteractionRequest))]
    [InlineData(typeof(ChatCompletionOptions))]
    [InlineData(typeof(AudioSpeechRequest))]
    [InlineData(typeof(OpenAIDecisionRequest))]
    public void Contract_libraries_do_not_reference_internal_assemblies(Type contractType)
    {
        Assert.DoesNotContain(contractType.Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("AIHappey.", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(typeof(VercelUnifiedMapper))]
    [InlineData(typeof(VercelUnifiedRequestExtensions))]
    [InlineData(typeof(ResponsesUnifiedMapper))]
    [InlineData(typeof(ResponsesUnifiedMapper.ResponseStreamMappingState))]
    [InlineData(typeof(ResponsesUnifiedMapper.ResponseReverseStreamState))]
    [InlineData(typeof(MessagesUnifiedMapper))]
    [InlineData(typeof(MessagesUnifiedMapper.MessagesStreamMappingState))]
    [InlineData(typeof(MessagesUnifiedMapper.MessagesReverseStreamMappingState))]
    [InlineData(typeof(MessagesUsageExtensions))]
    [InlineData(typeof(InteractionsUnifiedMapper))]
    [InlineData(typeof(InteractionsUnifiedMapper.InteractionStreamMappingState))]
    [InlineData(typeof(ChatCompletionsUnifiedMapper))]
    [InlineData(typeof(ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState))]
    public void Unified_mapping_implementations_are_owned_by_core(Type mappingType)
    {
        Assert.Equal("AIHappey.Core", mappingType.Assembly.GetName().Name);
    }

    [Fact]
    public void Reusable_request_helpers_remain_in_the_vercel_library()
    {
        Assert.Same(typeof(ChatRequest).Assembly, typeof(RequestExtensions).Assembly);
    }

    [Theory]
    [InlineData(typeof(MessagesRequest), typeof(MessagesContentJsonConverter))]
    [InlineData(typeof(MessagesRequest), typeof(MessagesJson))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionsInputJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionStepJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionContentJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionStepJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionToolJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionAgentConfigJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionStreamEventJsonConverter))]
    [InlineData(typeof(InteractionRequest), typeof(InteractionJson))]
    [InlineData(typeof(InteractionRequest), typeof(AIHappey.Interactions.ProviderMetadataExtensions))]
    [InlineData(typeof(InteractionRequest), typeof(HttpExtensions))]
    [InlineData(typeof(InteractionRequest), typeof(IInteractionTransportObserver))]
    [InlineData(typeof(ChatCompletionOptions), typeof(ChatMessageContentExtensions))]
    public void Reusable_helpers_and_converters_remain_in_their_contract_library(Type contractType, Type helperType)
    {
        Assert.Same(contractType.Assembly, helperType.Assembly);
    }

    [Theory]
    [InlineData(typeof(AudioSpeechRequest))]
    [InlineData(typeof(OpenAITranscriptionRequest))]
    [InlineData(typeof(OpenAITranscriptionTokenUsage))]
    [InlineData(typeof(OpenAIImageGenerationRequest))]
    [InlineData(typeof(OpenAIImageEditRequest))]
    [InlineData(typeof(OpenAIEmbeddingRequest))]
    [InlineData(typeof(ModelResponse))]
    [InlineData(typeof(OpenAiProviderMetadata))]
    [InlineData(typeof(OpenAiRealtimeProviderMetadata))]
    [InlineData(typeof(ShellEnvironmentUnionConverter))]
    [InlineData(typeof(LiveSessionRequest))]
    [InlineData(typeof(LiveRequestTransportJsonConverter))]
    [InlineData(typeof(AIHappey.Core.Providers.OpenAI.OpenAiRealtimeResponse))]
    public void OpenAI_contracts_are_owned_by_the_contract_library_not_either_backend_assembly(Type contractType)
    {
        Assert.Equal("AIHappey.OpenAI", contractType.Assembly.GetName().Name);
        Assert.Null(typeof(AIHappey.Core.Contracts.IModelProvider).Assembly.GetType(contractType.FullName!));
        Assert.Null(typeof(AIHappey.Common.Model.RealtimeRequest).Assembly.GetType(contractType.FullName!));
    }

    [Theory]
    [InlineData(typeof(AIHappey.Core.Extensions.LiveSessionExtensions))]
    [InlineData(typeof(AIHappey.Core.Extensions.RealtimeSessionMapping))]
    [InlineData(typeof(AIHappey.Core.AI.ModelProviderDecisionExtensions))]
    [InlineData(typeof(AIHappey.Core.AI.ModelProviderEmbeddingCompatibilityExtensions))]
    public void OpenAI_runtime_transports_and_adapters_remain_in_core(Type runtimeType)
    {
        Assert.Equal("AIHappey.Core", runtimeType.Assembly.GetName().Name);
    }
}
