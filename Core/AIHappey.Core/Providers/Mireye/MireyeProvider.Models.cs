using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Mireye;

public partial class MireyeProvider
{
    private const string MireyeAskModel = "ask";

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(
        [
            new Model
            {
                Id = MireyeAskModel.ToModelId(GetIdentifier()),
                Name = "Mireye Earth Ask",
                OwnedBy = nameof(Mireye),
                Type = "language",
                Description = "Natural-language geospatial Q&A over a US address or coordinate with field-level provenance.",
                Tags = ["agent", "geospatial", "citations", "streaming"]
            }
        ]);
}
