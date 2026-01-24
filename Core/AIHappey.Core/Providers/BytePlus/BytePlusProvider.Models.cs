using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(ByteDanceModels);
    }

    public static IReadOnlyList<Model> ByteDanceModels =>
[
     new()
    {
        Id = "byteplus/glm-4-7-251222",
        Name = "GLM-4.7",
        Type = "language",
        Description = "GLM-4.7 is Z.AI’s flagship model, with stronger coding, more reliable multi-step reasoning, and improved agentic performance, conversation quality, and front-end output.",
    },
    new()
    {
        Id = "byteplus/seed-1.8-251228",
        Name = "ByteDance-Seed-1.8",
        Type = "language",
        OwnedBy = "ByteDance"
    },
     new()
    {
        Id = "byteplus/seed-translation-250915",
        Name = "ByteDance-Seed-Translation",
        Type = "language",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/seed-1.6-250915",
        Name = "ByteDance-Seed-1.6",
        Type = "language",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/seed-1.6-flash-250715",
        Name = "bytedance-Seed-1.6-flash",
        Type = "language",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/seedance-1.0-pro-250528",
        Name = "ByteDance-Seedance-1.0-pro",
        Type = "video",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/seedream-3-0-t2i-250415",
        Name = "ByteDance-Seedream-3.0-t2i",
        Type = "image",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/kimi-k2-250711",
        Name = "Kimi-K2",
        Type = "language",
        OwnedBy = "Moonshot"
    },
    new()
    {
        Id = "byteplus/gpt-oss-120b-250805",
        Name = "GPT-OSS-120B",
        Type = "language",
        OwnedBy = "OpenAI"
    },
    new()
    {
        Id = "byteplus/seededit-3.0-i2i-250628",
        Name = "ByteDance-SeedEdit-3.0-i2i",
        Type = "image",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/skylark-pro-250415",
        Name = "Skylark-pro",
        Type = "language",
        OwnedBy = "Skylark"
    },
    new()
    {
        Id = "byteplus/skylark-vision-250515",
        Name = "Skylark-vision",
        Type = "language",
        OwnedBy = "Skylark"
    },
    new()
    {
        Id = "byteplus/seedream-4-5-251128",
        Name = "ByteDance-Seedream-4.5",
        Type = "image",
        OwnedBy = "ByteDance"
    },
    new()
    {
        Id = "byteplus/seedream-4-0-250828",
        Name = "ByteDance-Seedream-4.0",
        Type = "image",
        OwnedBy = "ByteDance"
    }
];

}