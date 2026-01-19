using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Responses;
using AIHappey.Common.Model.Responses.Streaming;
using AIHappey.Core.Models;

namespace AIHappey.Core.ModelProviders;

public interface IApiKeyResolver
{
    string? Resolve(string provider);
}
