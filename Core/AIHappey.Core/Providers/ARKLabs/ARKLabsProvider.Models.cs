using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ARKLabs;

public partial class ARKLabsProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return ARKLabsModels;
    }

    public static IReadOnlyList<Model> ARKLabsModels =>
        [
            // ===== Meta =====
            new()
            {
                Id = "arklabs/meta/Llama-3.1-8B-Instruct",
                Name = "Llama 3.1 8B Instruct",
                Type = "language",
                OwnedBy = "meta",
                Pricing = new ModelPricing
                {
                    Input = 0.29m,
                    Output = 0.29m
                }
            },

            // ===== Qwen =====
            new()
            {
                Id = "arklabs/Qwen/Qwen2.5-32B-Instruct",
                Name = "Qwen 2.5 32B Instruct",
                Type = "language",
                OwnedBy = "qwen",
                Pricing = new ModelPricing
                {
                    Input = 1.79m,
                    Output = 1.79m
                }
            },
            new()
            {
                Id = "arklabs/Qwen/Qwen2.5-Coder-32B-Instruct",
                Name = "Qwen 2.5 Coder 32B Instruct",
                Type = "language",
                OwnedBy = "qwen",
                Pricing = new ModelPricing
                {
                    Input = 1.79m,
                    Output = 1.79m
                }
            },
            new()
            {
                Id = "arklabs/Qwen/Qwen2.5-VL-7B-Instruct",
                Name = "Qwen 2.5 VL 7B Instruct",
                Type = "language",
                OwnedBy = "qwen",
                Pricing = new ModelPricing
                {
                    Input = 0.29m,
                    Output = 0.29m
                }
            },

            // ===== SpeakLeash =====
            new()
            {
                Id = "arklabs/speakleash/Bielik-11B-v2.6-Instruct",
                Name = "Bielik 11B v2.6 Instruct",
                Type = "language",
                OwnedBy = "speakleash",
                Pricing = new ModelPricing
                {
                    Input = 0.49m,
                    Output = 0.49m
                }
            },

            // ===== OpenAI OSS =====
             new()
             {
                 Id = "arklabs/openai/gpt-oss-20b",
                 Name = "GPT OSS 20B",
                Type = "language",
                OwnedBy = "openai",
                Pricing = new ModelPricing
                {
                    Input = 0.49m,
                     Output = 0.49m
                 }
             },

            // ===== OpenAI =====
            new()
            {
                Id = "arklabs/openai/whisper-large-v3-turbo",
                Name = "whisper-large-v3-turbo",
                Type = "transcription",
                OwnedBy = "openai",
                Pricing = new ModelPricing
                {
                    Output = 0.0144m
                }
            },

             // ===== Stability AI =====
             new()
             {
                Id = "arklabs/stabilityai/stable-diffusion-3.5-large",
                Name = "Stable Diffusion 3.5 Large",
                Type = "image",
                OwnedBy = "stabilityai",
                Pricing = new ModelPricing
                {
                    Output = 0.019m
                }
            },
        ];


}
