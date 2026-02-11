using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Gladia;

public partial class GladiaProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        return await Task.FromResult(new List<Model>()
       {
           new()
           {
               Id = "solaria-1".ToModelId(GetIdentifier()),
               Name = "Solaria",
               Tags = ["real-time"],
               Type = "transcription"
           }
       });
    }
}
