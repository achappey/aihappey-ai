using System.Reflection;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Decisions;

public class DecisionExtensionTests
{
    [Fact]
    public async Task BothExtensions_ThrowNotSupportedWithoutCallingProvider()
    {
        var provider = DispatchProxy.Create<IModelProvider, RejectProviderCalls>();

        var openAI = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest { Model = "model", Input = "text" }));
        var vercel = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await provider.DecisionRequestAsync(new DecisionRequest { Model = "model", State = "text" }));

        Assert.Contains("OpenAI-compatible", openAI.Message);
        Assert.Contains("Vercel-compatible", vercel.Message);
        Assert.DoesNotContain(typeof(IModelProvider).GetMethods(), m => m.Name.Contains("Decision"));
    }

    public class RejectProviderCalls : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("The Decisions stubs must not invoke the provider.");
    }
}
