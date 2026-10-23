using System.Reflection;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Decisions;

public class DecisionExtensionTests
{
    [Fact]
    public async Task BothContracts_InvokeProviderWithOriginalRequestsAndCancellation()
    {
        var provider = DispatchProxy.Create<IModelProvider, RecordingProviderCalls>();
        var recording = (RecordingProviderCalls)(object)provider;
        using var cancellation = new CancellationTokenSource();
        var openAIRequest = new OpenAIDecisionRequest { Model = "model", Input = "text" };
        var vercelRequest = new DecisionRequest { Model = "model", State = "text" };

        var openAI = await provider.OpenAIDecisionRequestAsync(openAIRequest, cancellation.Token);
        var vercel = await provider.DecisionRequestAsync(vercelRequest, cancellation.Token);

        Assert.Same(recording.OpenAIResponse, openAI);
        Assert.Same(recording.VercelResponse, vercel);
        Assert.Collection(recording.Calls,
            call =>
            {
                Assert.Equal(nameof(IModelProvider.OpenAIDecisionRequestAsync), call.Method.Name);
                Assert.Equal(typeof(IModelProvider), call.Method.DeclaringType);
                Assert.Equal(typeof(Task<OpenAIDecisionResponse>), call.Method.ReturnType);
                Assert.Equal(2, call.Arguments.Length);
                Assert.Same(openAIRequest, call.Arguments[0]);
                Assert.Equal(cancellation.Token, Assert.IsType<CancellationToken>(call.Arguments[1]));
            },
            call =>
            {
                Assert.Equal(nameof(IModelProvider.DecisionRequestAsync), call.Method.Name);
                Assert.Equal(typeof(IModelProvider), call.Method.DeclaringType);
                Assert.Equal(typeof(Task<DecisionResponse>), call.Method.ReturnType);
                Assert.Equal(2, call.Arguments.Length);
                Assert.Same(vercelRequest, call.Arguments[0]);
                Assert.Equal(cancellation.Token, Assert.IsType<CancellationToken>(call.Arguments[1]));
            });
    }

    public class RecordingProviderCalls : DispatchProxy
    {
        public OpenAIDecisionResponse OpenAIResponse { get; } = new() { Model = "model" };
        public DecisionResponse VercelResponse { get; } = new();
        public List<(MethodInfo Method, object?[] Arguments)> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            Calls.Add((targetMethod, args));
            return targetMethod.Name switch
            {
                nameof(IModelProvider.OpenAIDecisionRequestAsync) => Task.FromResult(OpenAIResponse),
                nameof(IModelProvider.DecisionRequestAsync) => Task.FromResult(VercelResponse),
                _ => throw new InvalidOperationException($"Unexpected provider call: {targetMethod.Name}")
            };
        }
    }
}
