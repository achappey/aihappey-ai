using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Gladia;

public partial class GladiaProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
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
