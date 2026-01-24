using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Runware does not provide a public list-models endpoint.
        // We expose a small curated list for discovery.
        var hasKey = !string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier()));
        if (!hasKey)
            return Task.FromResult<IEnumerable<Model>>([]);

        return Task.FromResult<IEnumerable<Model>>(
        [
            new() {
                OwnedBy = "OpenAI",
            Type = "image",
            Name = "DALL·E 2",
            Id = "openai:2@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "OpenAI",
            Type = "image",
            Name = "DALL·E 3",
            Id = "openai:2@3".ToModelId(GetIdentifier()) },
              new() {
                OwnedBy = "OpenAI",
            Type = "image",
            Name = "GPT Image 1",
            Id = "openai:1@1".ToModelId(GetIdentifier()) },
              new() {
            OwnedBy = "OpenAI",
            Type = "image",
            Name = "GPT Image 1 Mini",
            Id = "openai:1@2".ToModelId(GetIdentifier()) },
               new() {
            OwnedBy = "OpenAI",
            Type = "image",
            Name = "GPT Image 1.5",
            Id = "openai:4@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Google",
            Type = "image",
             Name = "Imagen 4.0 Ultra",
             Id = "google:2@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Google",
            Type = "image",
             Name = "Imagen 4.0 Fast",
             Id = "google:2@3".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Google",
            Type = "image",
             Name = "Gemini Flash Image 2.5 (Nano Banana)",
             Id = "google:4@1".ToModelId(GetIdentifier()) },
               new() {
                 OwnedBy = "Google",
              Type = "image",
              Name = "Gemini 3 Pro Image Preview (Nano Banana 2 Pro)",
              Id = "google:4@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Vidu",
                Type = "image",
                Name = "Vidu Q1 Image",
                Id = "vidu:q1@image".ToModelId(GetIdentifier()) },

            new() {
                OwnedBy = "Runway",
                Type = "image",
                Name = "Runway Gen-4 Image",
                Id = "runway:4@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Runway",
                Type = "image",
                Name = "Runway Gen-4 Image Turbo",
                Id = "runway:4@2".ToModelId(GetIdentifier()) },

            new() {
                OwnedBy = "Alibaba",
                Type = "image",
                Name = "Wan 2.5 Preview Image",
                Id = "alibaba:wan@2.5-image".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "KlingAI",
                Type = "image",
                Name = "Kling Image O1",
                Id = "klingai:kling-image@o1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "PrunaAI",
                Type = "image",
                Name = "P-Image",
                Id = "prunaai:1@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "PrunaAI",
                Type = "image",
                Name = "P-Image-Edit",
                Id = "prunaai:2@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "ImagineArt",
                Type = "image",
                Name = "ImagineArt 1.5",
                Id = "imagineart:1@5".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 1.0",
                Id = "ideogram:1@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 1.0 Remix",
                Id = "ideogram:1@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 2a",
                Id = "ideogram:2@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 2a Remix",
                Id = "ideogram:2@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 2.0",
                Id = "ideogram:3@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 2.0 Remix",
                Id = "ideogram:3@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 3.0",
                Id = "ideogram:4@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Ideogram",
                Type = "image",
                Name = "Ideogram 3.0 Remix",
                Id = "ideogram:4@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "ByteDance",
                Type = "image",
                Name = "SeedEdit 3.0",
                Id = "bytedance:4@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "ByteDance",
                Type = "image",
                Name = "Seedream 4.0",
                Id = "bytedance:5@0".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "ByteDance",
                Type = "image",
                Name = "Seedream 4.5",
                Id = "bytedance:seedream@4.5".ToModelId(GetIdentifier()) },

            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 1.1 Mini",
                Id = "sourceful:1@0".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 1.1",
                Id = "sourceful:1@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 1.1 Pro",
                Id = "sourceful:1@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 2 Preview Standard",
                Id = "sourceful:2@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 2 Preview Fast",
                Id = "sourceful:2@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 2 Preview Max",
                Id = "sourceful:2@3".ToModelId(GetIdentifier()) },

            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.1.1 Pro",
                Id = "bfl:2@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.1.1 Pro Ultra",
                Id = "bfl:2@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.1 Fill Pro (Inpainting)",
                Id = "bfl:1@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.1 Expand Pro (Outpainting)",
                Id = "bfl:1@3".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.1 Kontext [pro]",
                Id = "bfl:3@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.1 Kontext [max]",
                Id = "bfl:4@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [dev]",
                Id = "runware:400@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [pro]",
                Id = "bfl:5@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [flex]",
                Id = "bfl:6@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [max]",
                Id = "bfl:7@1".ToModelId(GetIdentifier()) },
             new() {
                OwnedBy = "Midjourney",
                Type = "image",
                Name = "Midjourney V6",
                Id = "midjourney:1@1".ToModelId(GetIdentifier()) },
                  new() {
                OwnedBy = "Midjourney",
                Type = "image",
                Name = "Midjourney V6.1",
                Id = "midjourney:2@1".ToModelId(GetIdentifier()) },
             new() {
                OwnedBy = "Midjourney",
                Type = "image",
                Name = "Midjourney V7",
                Id = "midjourney:3@1".ToModelId(GetIdentifier()) },

            new() {
                OwnedBy = "Bria",
                Type = "image",
                Name = "Bria 3.2",
                Id = "bria:10@1".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Bria",
                Type = "image",
                Name = "Bria FIBO",
                Id = "bria:20@1".ToModelId(GetIdentifier()) },
        ]);
    }

}

