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
    public void Unified_mapping_implementations_are_owned_by_core(Type mappingType)
    {
        Assert.Equal("AIHappey.Core", mappingType.Assembly.GetName().Name);
    }

    [Fact]
    public void Reusable_request_helpers_remain_in_the_vercel_library()
    {
        Assert.Same(typeof(ChatRequest).Assembly, typeof(RequestExtensions).Assembly);
    }
}
