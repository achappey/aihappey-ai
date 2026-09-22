using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OCRSkill;

public partial class OCRSkillProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>
        ([
            new Model
            {
                Id = $"{GetIdentifier()}/{OcrModelId}",
                Name = "OCRSkill OCR",
                OwnedBy = GetIdentifier(),
                Type = "language",
                Description = "OCRSkill document and image OCR. Returns Markdown by default and typed JSON for supported structured fields."
            }
        ]);
}
