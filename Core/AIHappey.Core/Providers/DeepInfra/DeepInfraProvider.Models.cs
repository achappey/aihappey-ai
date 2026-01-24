using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>([.. DeepInfraLanguageModels,
            .. DeepInfraImageModels,
            .. DeepInfraRerankModels,
            ..DeepInfraSpeechModels,
            ..DeepInfraTranscriptionModels,
            ]);
    }

    public static IReadOnlyList<Model> DeepInfraLanguageModels =>
    [
        new()
        {
            Id = "nvidia/Nemotron-3-Nano-30B-A3B".ToModelId("deepinfra"),
            Name = "Nemotron-3-Nano-30B-A3B",
            Type = "language",
            OwnedBy = "NVIDIA",
            Description = "NVIDIA Nemotron 3 Nano (30B A3B)."
        },

        new() { Id = "deepseek-ai/DeepSeek-V3.2".ToModelId("deepinfra"), Name = "DeepSeek-V3.2", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3.1".ToModelId("deepinfra"), Name = "DeepSeek-V3.1", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3.1-Terminus".ToModelId("deepinfra"), Name = "DeepSeek-V3.1-Terminus", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3-0324".ToModelId("deepinfra"), Name = "DeepSeek-V3-0324", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-V3".ToModelId("deepinfra"), Name = "DeepSeek-V3", Type = "language", OwnedBy = "DeepSeek" },

        new() { Id = "deepseek-ai/DeepSeek-R1-0528".ToModelId("deepinfra"), Name = "DeepSeek-R1-0528", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-R1".ToModelId("deepinfra"), Name = "DeepSeek-R1", Type = "language", OwnedBy = "DeepSeek" },
        new() { Id = "deepseek-ai/DeepSeek-R1-0528-Turbo".ToModelId("deepinfra"), Name = "DeepSeek-R1-0528-Turbo", Type = "language", OwnedBy = "DeepSeek" },

        new() { Id = "moonshotai/Kimi-K2-Thinking".ToModelId("deepinfra"), Name = "Kimi-K2-Thinking", Type = "language", OwnedBy = "MoonshotAI" },
        new() { Id = "moonshotai/Kimi-K2-Instruct-0905".ToModelId("deepinfra"), Name = "Kimi-K2-Instruct-0905", Type = "language", OwnedBy = "MoonshotAI" },

        new() { Id = "openai/gpt-oss-120b".ToModelId("deepinfra"), Name = "gpt-oss-120b", Type = "language", OwnedBy = "OpenAI" },
        new() { Id = "openai/gpt-oss-20b".ToModelId("deepinfra"), Name = "gpt-oss-20b", Type = "language", OwnedBy = "OpenAI" },

        new() { Id = "Qwen/Qwen3-Next-80B-A3B-Instruct".ToModelId("deepinfra"), Name = "Qwen3-Next-80B-A3B-Instruct", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-Next-80B-A3B-Thinking".ToModelId("deepinfra"), Name = "Qwen3-Next-80B-A3B-Thinking", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct".ToModelId("deepinfra"), Name = "Qwen3-Coder-480B-A35B-Instruct", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct-Turbo".ToModelId("deepinfra"), Name = "Qwen3-Coder-480B-A35B-Instruct-Turbo", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-235B-A22B-Instruct-2507".ToModelId("deepinfra"), Name = "Qwen3-235B-A22B-Instruct-2507", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-235B-A22B-Thinking-2507".ToModelId("deepinfra"), Name = "Qwen3-235B-A22B-Thinking-2507", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-32B".ToModelId("deepinfra"), Name = "Qwen3-32B", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-30B-A3B".ToModelId("deepinfra"), Name = "Qwen3-30B-A3B", Type = "language", OwnedBy = "Qwen" },
        new() { Id = "Qwen/Qwen3-14B".ToModelId("deepinfra"), Name = "Qwen3-14B", Type = "language", OwnedBy = "Qwen" },

        new() { Id = "MiniMaxAI/MiniMax-M2".ToModelId("deepinfra"), Name = "MiniMax-M2", Type = "language", OwnedBy = "MiniMax" },
        new() { Id = "MiniMaxAI/MiniMax-M2.1".ToModelId("deepinfra"), Name = "MiniMax-M2.1", Type = "language", OwnedBy = "MiniMax" },

        new() { Id = "anthropic/claude-4-sonnet".ToModelId("deepinfra"),
            Name = "claude-4-sonnet",
            Description = "Anthropic's mid-size model with superior intelligence for high-volume uses in coding, in-depth research, agents, & more.",
            Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "anthropic/claude-3-7-sonnet-latest".ToModelId("deepinfra"),
            Name = "claude-3-7-sonnet-latest",
            Type = "language", OwnedBy = "Anthropic" },
        new() { Id = "anthropic/claude-4-opus".ToModelId("deepinfra"),
            Name = "claude-4-opus",
            Description = "Anthropic’s most powerful model yet and the state-of-the-art coding model. It delivers sustained performance on long-running tasks that require focused effort and thousands of steps, significantly expanding what AI agents can solve. Claude Opus 4 is ideal for powering frontier agent products and features.",
            Type = "language", OwnedBy = "Anthropic" },

        new() { Id = "google/gemini-2.5-pro".ToModelId("deepinfra"),
            Name = "gemini-2.5-pro",
            Description = "Gemini 2.5 Pro is Google's the most advanced thinking model, designed to tackle increasingly complex problems. Gemini 2.5 Pro leads common benchmarks by meaningful margins and showcases strong reasoning and code capabilities. Gemini 2.5 models are thinking models, capable of reasoning through their thoughts before responding, resulting in enhanced performance and improved accuracy. The Gemini 2.5 Pro model is now available on DeepInfra.",
            Type = "language", OwnedBy = "Google" },
        new() { Id = "google/gemini-2.5-flash".ToModelId("deepinfra"),
            Name = "gemini-2.5-flash",
            Description = "Gemini 2.5 Flash is Google's latest thinking model, designed to tackle increasingly complex problems. It's capable of reasoning through their thoughts before responding, resulting in enhanced performance and improved accuracy. Gemini 2.5 Flash: best for balancing reasoning and speed.",
            Type = "language", OwnedBy = "Google" },
        new() { Id = "google/gemma-3-27b-it".ToModelId("deepinfra"),
            Name = "gemma-3-27b-it",
            Description = "Gemma 3 introduces multimodality, supporting vision-language input and text outputs. It handles context windows up to 128k tokens, understands over 140 languages, and offers improved math, reasoning, and chat capabilities, including structured outputs and function calling. Gemma 3 27B is Google's latest open source model, successor to Gemma 2.",
            Type = "language", OwnedBy = "Google" },
        new() { Id = "google/gemma-3-12b-it".ToModelId("deepinfra"),
            Name = "gemma-3-12b-it",
            Description = "Gemma 3 introduces multimodality, supporting vision-language input and text outputs. It handles context windows up to 128k tokens, understands over 140 languages, and offers improved math, reasoning, and chat capabilities, including structured outputs and function calling. Gemma 3 27B is Google's latest open source model, successor to Gemma 2.",
            Type = "language", OwnedBy = "Google" },
        new() { Id = "google/gemma-3-4b-it".ToModelId("deepinfra"),
            Name = "gemma-3-4b-it",
            Description = "Gemma 3 introduces multimodality, supporting vision-language input and text outputs. It handles context windows up to 128k tokens, understands over 140 languages, and offers improved math, reasoning, and chat capabilities, including structured outputs and function calling. Gemma 3 27B is Google's latest open source model, successor to Gemma 2.",
            Type = "language", OwnedBy = "Google" },


        new() { Id = "mistralai/Mixtral-8x7B-Instruct-v0.1".ToModelId("deepinfra"),
            Name = "Mixtral-8x7B-Instruct-v0.1",
            Description = "Mixtral is mixture of expert large language model (LLM) from Mistral AI. This is state of the art machine learning model using a mixture 8 of experts (MoE) 7b models. During inference 2 expers are selected. This architecture allows large models to be fast and cheap at inference. The Mixtral-8x7B outperforms Llama 2 70B on most benchmarks.",
            Type = "language", OwnedBy = "Mistral" },
        new() { Id = "mistralai/Mistral-Small-3.2-24B-Instruct-2506".ToModelId("deepinfra"),
            Name = "Mistral-Small-3.2-24B-Instruct-2506",
            Description = "Mistral-Small-3.2-24B-Instruct is a drop-in upgrade over the 3.1 release, with markedly better instruction following, roughly half the infinite-generation errors, and a more robust function-calling interface—while otherwise matching or slightly improving on all previous text and vision benchmarks.",
            Type = "language", OwnedBy = "Mistral" },
        new() { Id = "mistralai/Mistral-Nemo-Instruct-2407".ToModelId("deepinfra"),
            Name = "Mistral-Nemo-Instruct-2407",
            Description = "12B model trained jointly by Mistral AI and NVIDIA, it significantly outperforms existing models smaller or similar in size.",
            Type = "language", OwnedBy = "Mistral" },
        new() { Id = "mistralai/Mistral-Small-24B-Instruct-2501".ToModelId("deepinfra"),
            Name = "Mistral-Small-24B-Instruct-2501",
            Description = "Mistral Small 3 is a 24B-parameter language model optimized for low-latency performance across common AI tasks. Released under the Apache 2.0 license, it features both pre-trained and instruction-tuned versions designed for efficient local deployment. The model achieves 81% accuracy on the MMLU benchmark and performs competitively with larger models like Llama 3.3 70B and Qwen 32B, while operating at three times the speed on equivalent hardware.",
            Type = "language", OwnedBy = "Mistral" },


        new() { Id = "nvidia/Llama-3.1-Nemotron-70B-Instruct".ToModelId("deepinfra"),
            Name = "Llama-3.1-Nemotron-70B-Instruct",
            Description = "Llama-3.1-Nemotron-70B-Instruct is a large language model customized by NVIDIA to improve the helpfulness of LLM generated responses to user queries. This model reaches Arena Hard of 85.0, AlpacaEval 2 LC of 57.6 and GPT-4-Turbo MT-Bench of 8.98, which are known to be predictive of LMSys Chatbot Arena Elo. As of 16th Oct 2024, this model is #1 on all three automatic alignment benchmarks (verified tab for AlpacaEval 2 LC), edging out strong frontier models such as GPT-4o and Claude 3.5 Sonnet.",
            Type = "language", OwnedBy = "NVIDIA" },
        new() { Id = "nvidia/Llama-3.3-Nemotron-Super-49B-v1.5".ToModelId("deepinfra"),
            Name = "Llama-3.3-Nemotron-Super-49B-v1.5",
            Description = "Llama-3.3-Nemotron-Super-49B-v1.5 is a large language model (LLM) optimized for advanced reasoning, conversational interactions, retrieval-augmented generation (RAG), and tool-calling tasks. Derived from Meta's Llama-3.3-70B-Instruct, it employs a Neural Architecture Search (NAS) approach, significantly enhancing efficiency and reducing memory requirements.",
            Type = "language", OwnedBy = "NVIDIA" },


        new() { Id = "NousResearch/Hermes-3-Llama-3.1-405B".ToModelId("deepinfra"),
            Name = "Hermes-3-Llama-3.1-405B",
            Description = "Hermes 3 is a cutting-edge language model that offers advanced capabilities in roleplaying, reasoning, and conversation. It's a fine-tuned version of the Llama-3.1 405B foundation model, designed to align with user needs and provide powerful control. Key features include reliable function calling, structured output, generalist assistant capabilities, and improved code generation. Hermes 3 is competitive with Llama-3.1 Instruct models, with its own strengths and weaknesses.",
            Type = "language", OwnedBy = "NousResearch" },
        new() { Id = "NousResearch/Hermes-3-Llama-3.1-70B".ToModelId("deepinfra"),
            Name = "Hermes-3-Llama-3.1-70B",
            Description = "Hermes 3 is a generalist language model with many improvements over Hermes 2, including advanced agentic capabilities, much better roleplaying, reasoning, multi-turn conversation, long context coherence, and improvements across the board.",
            Type = "language", OwnedBy = "NousResearch" },


        new() { Id = "meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8".ToModelId("deepinfra"),
            Name = "Llama-4-Maverick-17B-128E-Instruct-FP8",
            Description = "The Llama 4 collection of models are natively multimodal AI models that enable text and multimodal experiences. These models leverage a mixture-of-experts architecture to offer industry-leading performance in text and image understanding. Llama 4 Maverick, a 17 billion parameter model with 128 experts",
            Type = "language", OwnedBy = "Meta" },
        new() { Id = "meta-llama/Llama-4-Scout-17B-16E-Instruct".ToModelId("deepinfra"),
            Name = "Llama-4-Scout-17B-16E-Instruct",
            Description = "The Llama 4 collection of models are natively multimodal AI models that enable text and multimodal experiences. These models leverage a mixture-of-experts architecture to offer industry-leading performance in text and image understanding. Llama 4 Scout, a 17 billion parameter model with 16 experts.",
            Type = "language", OwnedBy = "Meta" },
        new() { Id = "meta-llama/Llama-3.3-70B-Instruct-Turbo".ToModelId("deepinfra"),
            Name = "Llama-3.3-70B-Instruct-Turbo",
            Description = "Llama 3.3-70B Turbo is a highly optimized version of the Llama 3.3-70B model, utilizing FP8 quantization to deliver significantly faster inference speeds with a minor trade-off in accuracy. The model is designed to be helpful, safe, and flexible, with a focus on responsible deployment and mitigating potential risks such as bias, toxicity, and misinformation. It achieves state-of-the-art performance on various benchmarks, including conversational tasks, language translation, and text generation.",
            Type = "language", OwnedBy = "Meta" },
        new() { Id = "meta-llama/Llama-Guard-4-12B".ToModelId("deepinfra"),
            Name = "Llama-Guard-4-12B",
            Description = "Llama Guard 4 is a natively multimodal safety classifier with 12 billion parameters trained jointly on text and multiple images. Llama Guard 4 is a dense architecture pruned from the Llama 4 Scout pre-trained model and fine-tuned for content safety classification. Similar to previous versions, it can be used to classify content in both LLM inputs (prompt classification) and in LLM responses (response classification). It itself acts as an LLM: it generates text in its output that indicates whether a given prompt or response is safe or unsafe, and if unsafe, it also lists the content categories violated.",
            Type = "language", OwnedBy = "Meta" },

        new() { Id = "microsoft/phi-4".ToModelId("deepinfra"),
            Name = "phi-4",
            Description = "Phi-4 is a model built upon a blend of synthetic datasets, data from filtered public domain websites, and acquired academic books and Q&A datasets. The goal of this approach was to ensure that small capable models were trained with data focused on high quality and advanced reasoning.",
            Type = "language", OwnedBy = "Microsoft" },


        new() { Id = "deepseek-ai/DeepSeek-OCR".ToModelId("deepinfra"), Name = "DeepSeek-OCR",
            Description = "DeepSeek-OCR as an initial investigation into the feasibility of compressing long contexts via optical 2D mapping. DeepSeek-OCR consists of two components: DeepEncoder and DeepSeek3B-MoE-A570M as the decoder. Specifically, DeepEncoder serves as the core engine, designed to maintain low activations under high-resolution input while achieving high compression ratios to ensure an optimal and manageable number of vision tokens. Experiments show that when the number of text tokens is within 10 times that of vision tokens (i.e., a compression ratio < 10x), the model can achieve decoding (OCR) precision of 97%. Even at a compression ratio of 20x, the OCR accuracy still remains at about 60%. This shows considerable promise for research areas such as historical long-context compression and memory forgetting mechanisms in LLMs.",
            Type = "language", OwnedBy = "DeepSeek" },

        new() { Id = "allenai/olmOCR-2-7B-1025".ToModelId("deepinfra"), Name = "olmOCR-2-7B-1025",
            Description = "olmOCR is a specialized AI tool that converts PDF documents into clean, structured text while preserving important formatting and layout information. What makes olmOCR particularly valuable for developers is its ability to handle challenging PDFs that traditional OCR tools struggle with—including complex layouts, poor-quality scans, handwritten text, and documents with mixed content types. Built on a fine-tuned 7B vision-language model, olmOCR provides enterprise-grade PDF processing at a fraction of the cost of proprietary solutions.",
            Type = "language", OwnedBy = "Allen AI" },

        new() { Id = "PaddlePaddle/PaddleOCR-VL-0.9B".ToModelId("deepinfra"), Name = "PaddleOCR-VL-0.9B",
            Description = "PaddleOCR-VL is a SOTA and resource-efficient model tailored for document parsing. Its core component is PaddleOCR-VL-0.9B, a compact yet powerful vision-language model (VLM) that integrates a NaViT-style dynamic resolution visual encoder with the ERNIE-4.5-0.3B language model to enable accurate element recognition. This innovative model efficiently supports 109 languages and excels in recognizing complex elements (e.g., text, tables, formulas, and charts), while maintaining minimal resource consumption. Through comprehensive evaluations on widely used public benchmarks and in-house benchmarks, PaddleOCR-VL achieves SOTA performance in both page-level document parsing and element-level recognition. It significantly outperforms existing solutions, exhibits strong competitiveness against top-tier VLMs, and delivers fast inference speeds. These strengths make it highly suitable for practical deployment in real-world scenarios.",
            Type = "language", OwnedBy = "PaddlePaddle" },
    ];

    public static IReadOnlyList<Model> DeepInfraSpeechModels =>
    [
          new() { Id = "ResembleAI/chatterbox-multilingual".ToModelId("deepinfra"),
            Name = "chatterbox-multilingual",
            Description = "Introducing Chatterbox Multilingual in 23 Languages! We're excited to introduce Chatterbox and Chatterbox Multilingual, Resemble AI's production-grade open source TTS models. Chatterbox Multilingual supports Arabic, Danish, German, Greek, English, Spanish, Finnish, French, Hebrew, Hindi, Italian, Japanese, Korean, Malay, Dutch, Norwegian, Polish, Portuguese, Russian, Swedish, Swahili, Turkish, Chinese out of the box. Licensed under MIT, Chatterbox has been benchmarked against leading closed-source systems like ElevenLabs, and is consistently preferred in side-by-side evaluations.",
            Type = "speech",
            OwnedBy = "Resemble AI" },

        new() { Id = "ResembleAI/chatterbox-turbo".ToModelId("deepinfra"),
            Name = "chatterbox-turbo",
            Description = "Chatterbox is a family of three state-of-the-art, open-source text-to-speech models by Resemble AI. We are excited to introduce Chatterbox-Turbo, our most efficient model yet. Built on a streamlined 350M parameter architecture, Turbo delivers high-quality speech with less compute and VRAM than our previous models. We have also distilled the speech-token-to-mel decoder, previously a bottleneck, reducing generation from 10 steps to just one, while retaining high-fidelity audio output. Paralinguistic tags are now native to the Turbo model, allowing you to use [cough], [laugh], [chuckle], and more to add distinct realism. While Turbo was built primarily for low-latency voice agents, it excels at narration and creative workflows. If you like the model but need to scale or tune it for higher accuracy, check out our competitively priced TTS service (link).",
            Type = "speech",
            OwnedBy = "Resemble AI" },

        new() { Id = "hexgrad/Kokoro-82M".ToModelId("deepinfra"), Name = "Kokoro-82M",
            Description = "Kokoro is an open-weight TTS model with 82 million parameters. Despite its lightweight architecture, it delivers comparable quality to larger models while being significantly faster and more cost-efficient. With Apache-licensed weights, Kokoro can be deployed anywhere from production environments to personal projects.",
            Type = "speech", OwnedBy = "Hexgrad" },

        new() { Id = "sesame/csm-1b".ToModelId("deepinfra"), Name = "csm-1b",
            Description = "CSM (Conversational Speech Model) is a speech generation model from Sesame that generates RVQ audio codes from text and audio inputs. The model architecture employs a Llama backbone and a smaller audio decoder that produces Mimi audio codes.",
            Type = "speech", OwnedBy = "Sesame" },

        new() { Id = "canopylabs/orpheus-3b-0.1-ft".ToModelId("deepinfra"),
            Name = "orpheus-3b-0.1-ft",
            Description = "Orpheus TTS is a state-of-the-art, Llama-based Speech-LLM designed for high-quality, empathetic text-to-speech generation. This model has been finetuned to deliver human-level speech synthesis, achieving exceptional clarity, expressiveness, and real-time streaming performances.",
            Type = "speech",
            OwnedBy = "Canopy Labs" },

        new() { Id = "Zyphra/Zonos-v0.1-transformer".ToModelId("deepinfra"),
            Name = "Zonos-v0.1-transformer",
            Description = "Zonos-v0.1 is a leading open-weight text-to-speech model trained on more than 200k hours of varied multilingual speech, delivering expressiveness and quality on par with—or even surpassing—top TTS providers. Our model enables highly natural speech generation from text prompts when given a speaker embedding or audio prefix, and can accurately perform speech cloning when given a reference clip spanning just a few seconds. The conditioning setup also allows for fine control over speaking rate, pitch variation, audio quality, and emotions such as happiness, fear, sadness, and anger. The model outputs speech natively at 44kHz.",
            Type = "speech",
            OwnedBy = "Zyphra" },

        new() { Id = "Zyphra/Zonos-v0.1-hybrid".ToModelId("deepinfra"),
            Name = "Zonos-v0.1-hybrid",
            Description = "Zonos-v0.1 is a leading open-weight text-to-speech model trained on more than 200k hours of varied multilingual speech, delivering expressiveness and quality on par with—or even surpassing—top TTS providers. Our model enables highly natural speech generation from text prompts when given a speaker embedding or audio prefix, and can accurately perform speech cloning when given a reference clip spanning just a few seconds. The conditioning setup also allows for fine control over speaking rate, pitch variation, audio quality, and emotions such as happiness, fear, sadness, and anger. The model outputs speech natively at 44kHz.",
            Type = "speech",
            OwnedBy = "Zyphra" },
    ];

    public static IReadOnlyList<Model> DeepInfraTranscriptionModels =>
    [
        new() { Id = "mistralai/Voxtral-Small-24B-2507".ToModelId("deepinfra"),
            Name = "Voxtral-Small-24B-2507",
            Description = "Voxtral Small is an enhancement of Mistral Small 3, incorporating state-of-the-art audio input capabilities while retaining best-in-class text performance. It excels at speech transcription, translation and audio understanding.",
            Type = "transcription",
            ContextWindow = 32_768,
            OwnedBy = "Mistral" },
        new() { Id = "mistralai/Voxtral-Mini-3B-2507".ToModelId("deepinfra"),
            Name = "Voxtral-Mini-3B-2507",
            Description = "Voxtral Mini is an enhancement of Ministral 3B, incorporating state-of-the-art audio input capabilities while retaining best-in-class text performance. It excels at speech transcription, translation and audio understanding.",
            Type = "transcription",
            ContextWindow = 32_768,
            OwnedBy = "Mistral" },
        new() { Id = "openai/whisper-large-v3-turbo".ToModelId("deepinfra"),
            Name = "whisper-large-v3-turbo",
            Description = "Whisper is a state-of-the-art model for automatic speech recognition (ASR) and speech translation, proposed in the paper 'Robust Speech Recognition via Large-Scale Weak Supervision' by Alec Radford et al. from OpenAI. Trained on >5M hours of labeled data, Whisper demonstrates a strong ability to generalise to many datasets and domains in a zero-shot setting. Whisper large-v3-turbo is a finetuned version of a pruned Whisper large-v3. In other words, it's the exact same model, except that the number of decoding layers have reduced from 32 to 4. As a result, the model is way faster, at the expense of a minor quality degradation.",
            Type = "transcription",
            OwnedBy = "OpenAI" },
        new() { Id = "openai/whisper-large-v3".ToModelId("deepinfra"),
            Name = "whisper-large-v3",
            Description = "Whisper is a general-purpose speech recognition model. It is trained on a large dataset of diverse audio and is also a multi-task model that can perform multilingual speech recognition as well as speech translation and language identification.",
            Type = "transcription",
            OwnedBy = "OpenAI" },
    ];

    public static IReadOnlyList<Model> DeepInfraImageModels =>
    [
        // ---- Bria ----
        new() { Id = "Bria/Bria-3.2".ToModelId("deepinfra"), Name = "Bria-3.2", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/Bria-3.2-vector".ToModelId("deepinfra"), Name = "Bria-3.2-vector", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/fibo".ToModelId("deepinfra"), Name = "fibo", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/enhance".ToModelId("deepinfra"), Name = "enhance", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/expand".ToModelId("deepinfra"), Name = "expand", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/blur_background".ToModelId("deepinfra"), Name = "blur background", Type = "image", OwnedBy = "Bria" },
        new() { Id = "Bria/erase_foreground".ToModelId("deepinfra"), Name = "erase foreground", Type = "image", OwnedBy = "Bria" },

        // ---- ByteDance ----
        new() { Id = "ByteDance/Seedream-4".ToModelId("deepinfra"), Name = "Seedream-4", Type = "image", OwnedBy = "ByteDance" },

        // ---- PrunaAI ----
        new() { Id = "PrunaAI/p-image".ToModelId("deepinfra"), Name = "p-image", Type = "image", OwnedBy = "PrunaAI" },

        // ---- Black Forest Labs ----
        new() { Id = "black-forest-labs/FLUX-1-dev".ToModelId("deepinfra"), Name = "FLUX-1-dev", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-1-schnell".ToModelId("deepinfra"), Name = "FLUX-1-schnell", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-1.1-pro".ToModelId("deepinfra"), Name = "FLUX-1.1-pro", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-2-dev".ToModelId("deepinfra"), Name = "FLUX-2-dev", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-2-pro".ToModelId("deepinfra"), Name = "FLUX-2-pro", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-2-max".ToModelId("deepinfra"), Name = "FLUX-2-max", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX-pro".ToModelId("deepinfra"), Name = "FLUX-pro", Type = "image", OwnedBy = "BlackForestLabs" },
        new() { Id = "black-forest-labs/FLUX.1-Kontext-dev".ToModelId("deepinfra"), Name = "FLUX.1-Kontext-dev", Type = "image", OwnedBy = "BlackForestLabs" },

        // ---- StabilityAI ----
        new() { Id = "stabilityai/sdxl-turbo".ToModelId("deepinfra"), Name = "sdxl-turbo", Type = "image", OwnedBy = "StabilityAI" },
        new() { Id = "Qwen/Qwen-Image-Edit".ToModelId("deepinfra"), Name = "Qwen-Image-Edit", Type = "image", OwnedBy = "Qwen" },
    ];

    public static IReadOnlyList<Model> DeepInfraRerankModels =>
[
    // ---- Qwen ----
    new()
        {
            Id = "Qwen/Qwen3-Reranker-0.6B".ToModelId("deepinfra"),
            Name = "Qwen3-Reranker-0.6B",
            Type = "reranking",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-Reranker-4B".ToModelId("deepinfra"),
            Name = "Qwen3-Reranker-4B",
            Type = "reranking",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-Reranker-8B".ToModelId("deepinfra"),
            Name = "Qwen3-Reranker-8B",
            Type = "reranking",
            OwnedBy = "Qwen"
        },
    ];

}

