
namespace AIHappey.Core.Providers.Replicate;

public static class ReplicateProviderImageModels
{
    public static readonly IReadOnlyList<(string Id, string Name, string Owner)> ImageModels =
   [
       // Black Forest Labs (FLUX)
    ("black-forest-labs/flux-pro", "FLUX Pro", "Black Forest Labs"),
    ("black-forest-labs/flux-1.1-pro-ultra", "FLUX Pro Ultra", "Black Forest Labs"),
    ("black-forest-labs/flux-1.1-pro-ultra-finetuned", "FLUX Pro Ultra (Finetuned)", "Black Forest Labs"),
    ("black-forest-labs/flux-dev", "FLUX Dev", "Black Forest Labs"),
    ("black-forest-labs/flux-schnell", "FLUX Schnell", "Black Forest Labs"),
    ("black-forest-labs/flux-fill-pro", "FLUX Fill Pro", "Black Forest Labs"),
    ("black-forest-labs/flux-kontext-pro", "FLUX Kontext Pro", "Black Forest Labs"),
    ("black-forest-labs/flux-kontext-max", "FLUX Kontext Max", "Black Forest Labs"),

    // Black Forest Labs – newer FLUX
    ("black-forest-labs/flux-2-pro", "FLUX 2 Pro", "Black Forest Labs"),
    ("black-forest-labs/flux-2-max", "FLUX 2 Max", "Black Forest Labs"),
    ("black-forest-labs/flux-2-dev", "FLUX 2 Dev", "Black Forest Labs"),
    ("black-forest-labs/flux-2-flex", "FLUX 2 Flex", "Black Forest Labs"),
    ("black-forest-labs/flux-pro-finetuned", "FLUX Pro (Finetuned)", "Black Forest Labs"),
    ("black-forest-labs/flux-krea-dev", "FLUX Krea Dev", "Black Forest Labs"),

    // Google (Imagen / Gemini Image)
    ("google/imagen-3", "Imagen 3", "Google"),
    ("google/imagen-3-fast", "Imagen 3 Fast", "Google"),
    ("google/imagen-4", "Imagen 4", "Google"),
    ("google/imagen-4-fast", "Imagen 4 Fast", "Google"),
    ("google/imagen-4-ultra", "Imagen 4 Ultra", "Google"),
    ("google/nano-banana", "Nano Banana", "Google"),
    ("google/nano-banana-pro", "Nano Banana Pro", "Google"),
    ("google/gemini-2.5-flash-image", "Gemini 2.5 Flash Image", "Google"),
    ("google/upscaler", "Google Image Upscaler", "Google"),

    // Ideogram
    ("ideogram-ai/ideogram-v2", "Ideogram v2", "Ideogram"),
    ("ideogram-ai/ideogram-v2-turbo", "Ideogram v2 Turbo", "Ideogram"),
    ("ideogram-ai/ideogram-v3-balanced", "Ideogram v3 Balanced", "Ideogram"),
    ("ideogram-ai/ideogram-v3-quality", "Ideogram v3 Quality", "Ideogram"),
    ("ideogram-ai/ideogram-v3-turbo", "Ideogram v3 Turbo", "Ideogram"),

    // Recraft
    ("recraft-ai/recraft-v3", "Recraft v3", "Recraft"),
    ("recraft-ai/recraft-v3-svg", "Recraft v3 SVG", "Recraft"),
    ("recraft-ai/recraft-vectorize", "Recraft Vectorize", "Recraft"),
    ("recraft-ai/recraft-crisp-upscale", "Recraft Crisp Upscale", "Recraft"),
    ("recraft-ai/recraft-creative-upscale", "Recraft Creative Upscale", "Recraft"),
    ("recraft-ai/recraft-remove-background", "Recraft Remove Background", "Recraft"),

    // Bria
    ("bria/image-3.2", "Bria Image 3.2", "Bria"),
    ("bria/generate-background", "Bria Background", "Bria"),
    ("bria/remove-background", "Bria Remove Background", "Bria"),
    ("bria/eraser", "Bria Eraser", "Bria"),
    ("bria/genfill", "Bria GenFill", "Bria"),
    ("bria/expand-image", "Bria Expand Image", "Bria"),
    ("bria/increase-resolution", "Bria Increase Resolution", "Bria"),

    // Qwen
    ("qwen/qwen-image", "Qwen Image", "Qwen"),
    ("qwen/qwen-image-2512", "Qwen Image 2512", "Qwen"),
    ("qwen/qwen-image-edit", "Qwen Image Edit", "Qwen"),
    ("qwen/qwen-image-edit-plus", "Qwen Image Edit Plus", "Qwen"),
    ("qwen/qwen-image-edit-2511", "Qwen Image Edit 2511", "Qwen"),
    ("qwen/qwen-image-edit-plus-lora", "Qwen Image Edit Plus (LoRA)", "Qwen"),

    // Stability AI
    ("stability-ai/stable-diffusion-3.5-large", "Stable Diffusion 3.5 Large", "Stability AI"),
    ("stability-ai/stable-diffusion-3.5-large-turbo", "Stable Diffusion 3.5 Large Turbo", "Stability AI"),
    ("stability-ai/stable-diffusion-3.5-medium", "Stable Diffusion 3.5 Medium", "Stability AI"),

    // Upscale / enhancement
    ("nightmareai/real-esrgan", "Real-ESRGAN", "NightmareAI"),
    ("topazlabs/image-upscale", "Topaz Image Upscale", "Topaz Labs"),
    ("philz1337x/crystal-upscaler", "Crystal Upscaler", "Philz1337x"),

    // PrunaAI
    ("prunaai/p-image", "P-Image", "PrunaAI"),
    ("prunaai/z-image-turbo", "Z-Image Turbo", "PrunaAI"),

    // ByteDance
    ("bytedance/seedream-4", "Seedream 4", "ByteDance"),
    ("bytedance/seedream-4.5", "Seedream 4.5", "ByteDance"),
    ("bytedance/dreamina-3.1", "Dreamina 3.1", "ByteDance"),
    ("bytedance/seedream-3", "Seedream 3", "ByteDance"),

    // Reve
    ("reve/create", "Reve Create", "Reve"),
    ("reve/edit", "Reve Edit", "Reve"),
    ("reve/remix", "Reve Remix", "Reve"),
    ("reve/edit-fast", "Reve Edit Fast", "Reve"),

    // Tencent
    ("tencent/hunyuan-image-3", "Hunyuan Image 3", "Tencent"),
    ("tencent/hunyuan-image-2.1", "Hunyuan Image 2.1", "Tencent"),

    // xAI
    ("xai/grok-2-image", "Grok 2 Image", "xAI"),

    // Runway
    ("runwayml/gen4-image", "Gen-4 Image", "Runway"),
    ("runwayml/gen4-image-turbo", "Gen-4 Image Turbo", "Runway"),

    // Luma
    ("luma/photon", "Photon", "Luma"),
    ("luma/photon-flash", "Photon Flash", "Luma"),
    ("luma/reframe-image", "Reframe Image", "Luma"),

    // Minimax
    ("minimax/image-01", "Minimax Image 01", "MiniMax"),

    // Wan
    ("wan-video/wan-2.2-image", "Wan 2.2 Image", "Wan"),
];

}

