using AIHappey.Responses;
using AIHappey.Responses.Mapping;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();
    }
}

