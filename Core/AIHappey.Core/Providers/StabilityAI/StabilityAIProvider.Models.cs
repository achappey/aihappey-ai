using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.StabilityAI;

public partial class StabilityAIProvider : IModelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return
        [
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Name = "Stable Image Ultra",
                Type = "image",
                Id = "stable-image-ultra".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Image Core",
                Id = "stable-image-core".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Large",
                Id = "sd3.5-large".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Large Turbo",
                Id = "sd3.5-large-turbo".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Medium",
                Id = "sd3.5-medium".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Flash",
                Id = "sd3.5-flash".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "speech",
                Name = "Stable Audio 2.0",
                Id = "stable-audio-2".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "speech",
                Name = "Stable Audio 2.5",
                Id = "stable-audio-2.5".ToModelId(GetIdentifier())
            }
        ];
    }
}
