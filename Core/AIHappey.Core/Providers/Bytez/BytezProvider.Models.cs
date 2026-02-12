using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Bytez;

public partial class BytezProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>([.. BytezLanguageModels, 
            .. BytezAudioModels, 
            .. BytezSpeechModels, .. BytezImageModels, .. BytezVideoModels]);
    }

    public static IReadOnlyList<Model> BytezVideoModels =>
    [
        // OpenAI
        new()
    {
        Id = "bytez/openai/sora-2",
        Name = "sora-2",
        Type = "video",
        OwnedBy = "OpenAI"
    },
    new()
    {
        Id = "bytez/openai/sora-2-pro",
        Name = "sora-2-pro",
        Type = "video",
        OwnedBy = "OpenAI"
    },

    // Google
    new()
    {
        Id = "bytez/google/veo-3.1-generate-preview",
        Name = "veo-3.1-generate-preview",
        Type = "video",
        OwnedBy = "Google"
    },
    new()
    {
        Id = "bytez/google/veo-3.1-fast-generate-preview",
        Name = "veo-3.1-fast-generate-preview",
        Type = "video",
        OwnedBy = "Google"
    },
    new()
    {
        Id = "bytez/google/veo-3.0-generate-001",
        Name = "veo-3.0-generate-001",
        Type = "video",
        OwnedBy = "Google"
    },
    new()
    {
        Id = "bytez/google/veo-3.0-fast-generate-001",
        Name = "veo-3.0-fast-generate-001",
        Type = "video",
        OwnedBy = "Google"
    },
    new()
    {
        Id = "bytez/google/veo-2.0-generate-001",
        Name = "veo-2.0-generate-001",
        Type = "video",
        OwnedBy = "Google"
    },

    // WAN
    new()
    {
        Id = "bytez/wan/v2.6/text-to-video",
        Name = "v2.6/text-to-video",
        Type = "video",
        OwnedBy = "WAN"
    },

    // FAL
    new()
    {
        Id = "bytez/veed/fabric-1.0/text",
        Name = "veed/fabric-1.0/text",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/kling-video/v2.6/pro/text-to-video",
        Name = "fal-ai/kling-video/v2.6/pro/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/pixverse/v5.5/text-to-video",
        Name = "fal-ai/pixverse/v5.5/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/ltx-2/text-to-video",
        Name = "fal-ai/ltx-2/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/ltx-2/text-to-video/fast",
        Name = "fal-ai/ltx-2/text-to-video/fast",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/hunyuan-video-v1.5/text-to-video",
        Name = "fal-ai/hunyuan-video-v1.5/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/sana-video",
        Name = "fal-ai/sana-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/longcat-video/text-to-video/720p",
        Name = "fal-ai/longcat-video/text-to-video/720p",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/longcat-video/text-to-video/480p",
        Name = "fal-ai/longcat-video/text-to-video/480p",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/vidu/q2/text-to-video",
        Name = "fal-ai/vidu/q2/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/krea-wan-14b/text-to-video",
        Name = "fal-ai/krea-wan-14b/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/wan-alpha",
        Name = "fal-ai/wan-alpha",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/veo3.1",
        Name = "fal-ai/veo3.1",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/veo3.1/fast",
        Name = "fal-ai/veo3.1/fast",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/sora-2/text-to-video",
        Name = "fal-ai/sora-2/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/sora-2/text-to-video/pro",
        Name = "fal-ai/sora-2/text-to-video/pro",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/ovi",
        Name = "fal-ai/ovi",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/wan-25-preview/text-to-video",
        Name = "fal-ai/wan-25-preview/text-to-video",
        Type = "video",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/infinitalk/single-text",
        Name = "fal-ai/infinitalk/single-text",
        Type = "video",
        OwnedBy = "FAL"
    }
    ];


    public static IReadOnlyList<Model> BytezSpeechModels =>
    [
        new()
    {
        Id = "bytez/openai/tts-1",
        Name = "tts-1",
        Type = "speech",
        OwnedBy = "OpenAI"
    },
    new()
    {
        Id = "bytez/openai/tts-1-hd",
        Name = "tts-1-hd",
        Type = "speech",
        OwnedBy = "OpenAI"
    },

    // FAL
    new()
    {
        Id = "bytez/fal-ai/minimax/voice-clone",
        Name = "fal-ai/minimax/voice-clone",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax/speech-02-turbo",
        Name = "fal-ai/minimax/speech-02-turbo",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax/speech-02-hd",
        Name = "fal-ai/minimax/speech-02-hd",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/dia-tts",
        Name = "fal-ai/dia-tts",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/orpheus-tts",
        Name = "fal-ai/orpheus-tts",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/vibevoice/0.5b",
        Name = "fal-ai/vibevoice/0.5b",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/elevenlabs/tts/turbo-v2.5",
        Name = "fal-ai/elevenlabs/tts/turbo-v2.5",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/chatterbox/text-to-speech/turbo",
        Name = "fal-ai/chatterbox/text-to-speech/turbo",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/maya",
        Name = "fal-ai/maya",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/maya/batch",
        Name = "fal-ai/maya/batch",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax/speech-2.6-turbo",
        Name = "fal-ai/minimax/speech-2.6-turbo",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax/speech-2.6-hd",
        Name = "fal-ai/minimax/speech-2.6-hd",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/index-tts-2/text-to-speech",
        Name = "fal-ai/index-tts-2/text-to-speech",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/kling-video/v1/tts",
        Name = "fal-ai/kling-video/v1/tts",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/chatterbox/text-to-speech/multilingual",
        Name = "fal-ai/chatterbox/text-to-speech/multilingual",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/vibevoice",
        Name = "fal-ai/vibevoice",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/vibevoice/7b",
        Name = "fal-ai/vibevoice/7b",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax/preview/speech-2.5-hd",
        Name = "fal-ai/minimax/preview/speech-2.5-hd",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax/preview/speech-2.5-turbo",
        Name = "fal-ai/minimax/preview/speech-2.5-turbo",
        Type = "speech",
        OwnedBy = "FAL"
    }
    ];


    public static IReadOnlyList<Model> BytezAudioModels =>
    [
        // Beatoven
        new()
    {
        Id = "bytez/beatoven/sound-effect-generation",
        Name = "sound-effect-generation",
        Type = "speech",
        OwnedBy = "Beatoven"
    },
    new()
    {
        Id = "bytez/beatoven/music-generation",
        Name = "music-generation",
        Type = "speech",
        OwnedBy = "Beatoven"
    },

    // FAL
    new()
    {
        Id = "bytez/fal-ai/minimax-music/v1.5",
        Name = "fal-ai/minimax-music/v1.5",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/stable-audio-25/text-to-audio",
        Name = "fal-ai/stable-audio-25/text-to-audio",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/elevenlabs/sound-effects/v2",
        Name = "fal-ai/elevenlabs/sound-effects/v2",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/elevenlabs/tts/eleven-v3",
        Name = "fal-ai/elevenlabs/tts/eleven-v3",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/lyria2",
        Name = "fal-ai/lyria2",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/ace-step",
        Name = "fal-ai/ace-step",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/ace-step/prompt-to-audio",
        Name = "fal-ai/ace-step/prompt-to-audio",
        Type = "speech",
        OwnedBy = "FAL"
    },
    new()
    {
        Id = "bytez/fal-ai/minimax-music/v2",
        Name = "fal-ai/minimax-music/v2",
        Type = "speech",
        OwnedBy = "FAL"
    },

    // Sonauto
    new()
    {
        Id = "bytez/sonauto/sonauto/v2/inpaint",
        Name = "sonauto/v2/inpaint",
        Type = "speech",
        OwnedBy = "Sonauto"
    },
    new()
    {
        Id = "bytez/sonauto/v2/inpaint",
        Name = "v2/inpaint",
        Type = "speech",
        OwnedBy = "Sonauto"
    }
    ];


    public static IReadOnlyList<Model> BytezImageModels =>
    [
        // OpenAI
        new() { Id = "bytez/openai/dall-e-2", Name = "dall-e-2", Type = "image", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/dall-e-3", Name = "dall-e-3", Type = "image", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-image-1", Name = "gpt-image-1", Type = "image", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-image-1.5", Name = "gpt-image-1.5", Type = "image", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-image-1-mini", Name = "gpt-image-1-mini", Type = "image", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gemini-2.5-flash-image", Name = "gemini-2.5-flash-image", Type = "image", OwnedBy = "Google" },
        new() { Id = "bytez/openai/gemini-3-pro-image-preview", Name = "gemini-3-pro-image-preview", Type = "image", OwnedBy = "Google" },
        new() { Id = "bytez/openai/images-4.0-generate-001", Name = "images-4.0-generate-001", Type = "image", OwnedBy = "Google" },
        new() { Id = "bytez/openai/images-4.0-fast-generate-001", Name = "images-4.0-fast-generate-001", Type = "image", OwnedBy = "Google" },
        new() { Id = "bytez/openai/images-4.0-ultra-generate-001", Name = "images-4.0-ultra-generate-001", Type = "image", OwnedBy = "Google" },
    ];

    public static IReadOnlyList<Model> BytezLanguageModels =>
    [
        // OpenAI
        new() { Id = "bytez/openai/gpt-4o-mini", Name = "gpt-4o-mini", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-4.1", Name = "gpt-4.1", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-4.1-mini", Name = "gpt-4.1-mini", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-4.1-nano", Name = "gpt-4.1-nano", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-4", Name = "gpt-4", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-3.5-turbo", Name = "gpt-3.5-turbo", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-3.5-turbo-1106", Name = "gpt-3.5-turbo-1106", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-3.5-turbo-16k", Name = "gpt-3.5-turbo-16k", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-4o", Name = "gpt-4o", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-5-mini", Name = "gpt-5-mini", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-5", Name = "gpt-5", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-5.1", Name = "gpt-5.1", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/gpt-5.2", Name = "gpt-5.2", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/o1", Name = "o1", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/o3", Name = "o3", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "bytez/openai/o4-mini", Name = "o4-mini", Type = "language", OwnedBy = "OpenAI" },

        // Google
        new() { Id = "bytez/google/gemini-2.5-flash", Name = "gemini-2.5-flash", Type = "language", OwnedBy = "Google" },
        new() { Id = "bytez/google/gemini-2.5-flash-lite", Name = "gemini-2.5-flash-lite", Type = "language", OwnedBy = "Google" },
        new() { Id = "bytez/google/gemini-2.5-pro", Name = "gemini-2.5-pro", Type = "language", OwnedBy = "Google" },
        new() { Id = "bytez/google/gemini-3-pro-preview", Name = "gemini-3-pro-preview", Type = "language", OwnedBy = "Google" },
        new() { Id = "bytez/google/gemini-3-flash-preview", Name = "gemini-3-flash-preview", Type = "language", OwnedBy = "Google" },

        // Anthropic
        new() { Id = "bytez/anthropic/claude-sonnet-4-5", Name = "claude-sonnet-4-5", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-haiku-4-5", Name = "claude-haiku-4-5", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-opus-4-1-20250805", Name = "claude-opus-4-1-20250805", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-opus-4-20250514", Name = "claude-opus-4-20250514", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-haiku-4-5-20251001", Name = "claude-haiku-4-5-20251001", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-sonnet-4-5-20250929", Name = "claude-sonnet-4-5-20250929", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-sonnet-4-20250514", Name = "claude-sonnet-4-20250514", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-3-5-haiku-20241022", Name = "claude-3-5-haiku-20241022", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-3-haiku-20240307", Name = "claude-3-haiku-20240307", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-3-7-sonnet-20250219", Name = "claude-3-7-sonnet-20250219", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-opus-4-5", Name = "claude-opus-4-5", Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "bytez/anthropic/claude-opus-4-6", Name = "claude-opus-4-6", Type = "language", OwnedBy = "Anthropic" },

        // Mistral
        new() { Id = "bytez/mistral/magistral-medium-2509", Name = "magistral-medium-2509", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/mistral-medium-2505", Name = "mistral-medium-2505", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/mistral-medium-2508", Name = "mistral-medium-2508", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/devstral-medium-2507", Name = "devstral-medium-2507", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/mistral-large-2411", Name = "mistral-large-2411", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/ministral-3b-2410", Name = "ministral-3b-2410", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/ministral-8b-2410", Name = "ministral-8b-2410", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/codestral-2508", Name = "codestral-2508", Type = "language", OwnedBy = "Mistral" },
        new() { Id = "bytez/mistral/pixtral-large-2411", Name = "pixtral-large-2411", Type = "language", OwnedBy = "Mistral" }
    ];

}