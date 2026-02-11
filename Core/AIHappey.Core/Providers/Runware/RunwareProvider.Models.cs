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
                OwnedBy = "Alibaba",
                Type = "image",
                Name = "Wan 2.6 Image",
                Id = "alibaba:wan@2.6-image".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Alibaba",
                Type = "image",
                Name = "Qwen Image 2512",
                Id = "alibaba:qwen-image@2512".ToModelId(GetIdentifier()) },



            new() {
                OwnedBy = "Twinflow",
                Type = "image",
                Name = "Twinflow Z Image Turbo",
                Id = "runware:twinflow-z-image-turbo@0".ToModelId(GetIdentifier()) },




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
                OwnedBy = "ImagineArt",
                Type = "image",
                Name = "ImagineArt 1.5 Pro",
                Id = "imagineart:1.5-pro@0".ToModelId(GetIdentifier()) },
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
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 2 Fast",
                Id = "sourceful:riverflow-2.0@fast".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Sourceful",
                Type = "image",
                Name = "Riverflow 2 Pro",
                Id = "sourceful:riverflow-2.0@pro".ToModelId(GetIdentifier()) },



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
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [klein] 4B",
                Id = "runware:400@4".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [klein] 9B",
                Id = "runware:400@2".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [klein] 4B Base",
                Id = "runware:400@5".ToModelId(GetIdentifier()) },
            new() {
                OwnedBy = "Black Forest Labs",
                Type = "image",
                Name = "FLUX.2 [klein] 9B Base",
                Id = "runware:400@3".ToModelId(GetIdentifier()) },



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



            new() {
                OwnedBy = "xAI",
                Type = "image",
                Name = "Grok Imagine Image",
                Id = "xai:grok-imagine@image".ToModelId(GetIdentifier()) },


            // =======================
            // VIDEO MODELS (1–34)
            // =======================

            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu Q1", Id = "vidu:1@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu 2.0", Id = "vidu:2@0".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu 1.5", Id = "vidu:1@5".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu Q1 Classic", Id = "vidu:1@0".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu Q2 Pro", Id = "vidu:3@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu Q2 Turbo", Id = "vidu:3@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu Q3 Turbo", Id = "vidu:4@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Vidu", Type = "video", Name = "Vidu Q3", Id = "vidu:4@1".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI V2.0 Master", Id = "klingai:4@3".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI V1.0 Pro", Id = "klingai:1@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI V1.6 Pro", Id = "klingai:3@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI V1.5 Pro", Id = "klingai:2@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI V2.1 Standard (I2V)", Id = "klingai:5@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI V2.1 Master", Id = "klingai:5@3".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Runway", Type = "video", Name = "Runway Gen-4 Turbo", Id = "runway:1@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Runway", Type = "video", Name = "Runway Aleph", Id = "runway:2@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Runway", Type = "video", Name = "Runway Gen-4.5", Id = "runway:1@2".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Google", Type = "video", Name = "Veo 3.0", Id = "google:3@0".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Google", Type = "video", Name = "Veo 3.1", Id = "google:3@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Google", Type = "video", Name = "Veo 3.0 Fast", Id = "google:3@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Google", Type = "video", Name = "Veo 3.1 Fast", Id = "google:3@3".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Google", Type = "video", Name = "Veo 2.0", Id = "google:2@0".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "OpenAI", Type = "video", Name = "Sora 2", Id = "openai:3@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "OpenAI", Type = "video", Name = "Sora 2 Pro", Id = "openai:3@2".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "PixVerse", Type = "video", Name = "PixVerse v4", Id = "pixverse:1@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "PixVerse", Type = "video", Name = "PixVerse v5", Id = "pixverse:1@5".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "PixVerse", Type = "video", Name = "PixVerse v5 Fast", Id = "pixverse:1@5-fast".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "PixVerse", Type = "video", Name = "PixVerse v5.6", Id = "pixverse:1@7".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "MiniMax", Type = "video", Name = "Hailuo 02", Id = "minimax:3@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "MiniMax", Type = "video", Name = "Hailuo 2.3", Id = "minimax:4@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "MiniMax", Type = "video", Name = "Hailuo 2.3 Fast", Id = "minimax:4@2".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "ByteDance", Type = "video", Name = "Seedance 1.5 Pro", Id = "bytedance:seedance@1.5-pro".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "ByteDance", Type = "video", Name = "Seedance 1.0 Pro", Id = "bytedance:2@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "ByteDance", Type = "video", Name = "Seedance 1.0 Pro Fast", Id = "bytedance:2@2".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "ByteDance", Type = "video", Name = "Seedance 1.0 Lite", Id = "bytedance:1@1".ToModelId(GetIdentifier()) },

            // =======================
            // VIDEO MODELS (35–68)
            // =======================

            new() { OwnedBy = "Alibaba", Type = "video", Name = "Wan 2.2 A14B", Id = "runware:200@6".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Alibaba", Type = "video", Name = "Wan 2.6 Flash", Id = "alibaba:wan@2.6-flash".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Alibaba", Type = "video", Name = "Wan 2.6", Id = "alibaba:wan@2.6".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Lightricks", Type = "video", Name = "LTX-2 Fast", Id = "lightricks:2@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Lightricks", Type = "video", Name = "LTX-2 Pro", Id = "lightricks:2@0".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Lightricks", Type = "video", Name = "LTX-2 Retake", Id = "lightricks:3@1".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "Lightricks", Type = "video", Name = "LTX-2", Id = "lightricks:ltx@2".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Creatify", Type = "video", Name = "Aurora v1 Fast", Id = "creatify:aurora@fast".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "KlingAI", Type = "video", Name = "Kling VIDEO 2.6 Pro", Id = "klingai:kling-video@2.6-pro".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "Kling O1 Standard", Id = "klingai:kling@o1-standard".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI Avatar 2.0 Pro", Id = "klingai:avatar@2.0-pro".ToModelId(GetIdentifier()) },
            new() { OwnedBy = "KlingAI", Type = "video", Name = "KlingAI 2.5 Turbo PRO", Id = "klingai:6@1".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Bytedance", Type = "video", Name = "OmniHuman-1.5", Id = "bytedance:5@2".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Google", Type = "video", Name = "Veo 3 Audio", Id = "google:3@0-audio".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Runware", Type = "video", Name = "Wan 2.6 Motion-Realism", Id = "runware:201@1".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "PixVerse", Type = "video", Name = "PixVerse Character Fusion", Id = "pixverse:character@1".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "Bria", Type = "video", Name = "Bria Video Background Removal", Id = "bria:51@1".ToModelId(GetIdentifier()) },

            new() { OwnedBy = "xAI", Type = "video", Name = "Grok Imagine Video", Id = "xai:grok-imagine@video".ToModelId(GetIdentifier()) },

        ]);
    }

}

