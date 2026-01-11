using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Replicate;

public static class ReplicateProviderLanguageModels
{
    public static readonly IReadOnlyList<(string Id, string Name, string Owner)> LanguageModels =
    [
        // ─────────────────────────────
        // OpenAI
        // ─────────────────────────────
        ("openai/gpt-5", "GPT-5", "OpenAI"),
    ("openai/gpt-5-mini", "GPT-5 Mini", "OpenAI"),
    ("openai/gpt-5-nano", "GPT-5 Nano", "OpenAI"),
    ("openai/gpt-5-structured", "GPT-5 Structured", "OpenAI"),

    ("openai/gpt-5.2", "GPT-5.2", "OpenAI"),
    ("openai/gpt-5.1", "GPT-5.1", "OpenAI"),
    ("openai/gpt-5-pro", "GPT-5 Pro", "OpenAI"),

    ("openai/gpt-4.1", "GPT-4.1", "OpenAI"),
    ("openai/gpt-4.1-mini", "GPT-4.1 Mini", "OpenAI"),
    ("openai/gpt-4.1-nano", "GPT-4.1 Nano", "OpenAI"),

    ("openai/gpt-4o", "GPT-4o", "OpenAI"),
    ("openai/gpt-4o-mini", "GPT-4o Mini", "OpenAI"),

    ("openai/o1", "o1 Reasoning", "OpenAI"),
    ("openai/o1-mini", "o1 Mini", "OpenAI"),
    ("openai/o4-mini", "o4 Mini", "OpenAI"),

    ("openai/gpt-oss-120b", "GPT-OSS 120B", "OpenAI"),
    ("openai/gpt-oss-20b", "GPT-OSS 20B", "OpenAI"),

    // ─────────────────────────────
    // Anthropic
    // ─────────────────────────────
    ("anthropic/claude-4.5-sonnet", "Claude 4.5 Sonnet", "Anthropic"),
    ("anthropic/claude-4.5-haiku", "Claude 4.5 Haiku", "Anthropic"),
    ("anthropic/claude-4-sonnet", "Claude 4 Sonnet", "Anthropic"),
    ("anthropic/claude-3.7-sonnet", "Claude 3.7 Sonnet", "Anthropic"),
    ("anthropic/claude-3.5-sonnet", "Claude 3.5 Sonnet", "Anthropic"),
    ("anthropic/claude-3.5-haiku", "Claude 3.5 Haiku", "Anthropic"),

    // ─────────────────────────────
    // xAI
    // ─────────────────────────────
    ("xai/grok-4", "Grok 4", "xAI"),

    // ─────────────────────────────
    // Meta (LLaMA)
    // ─────────────────────────────
    ("meta/meta-llama-3.1-405b-instruct", "LLaMA 3.1 405B Instruct", "Meta"),
    ("meta/llama-4-scout-instruct", "LLaMA 4 Scout Instruct", "Meta"),
    ("meta/llama-4-maverick-instruct", "LLaMA 4 Maverick Instruct", "Meta"),
    ("meta/meta-llama-3-70b-instruct", "LLaMA 3 70B Instruct", "Meta"),
    ("meta/meta-llama-3-8b-instruct", "LLaMA 3 8B Instruct", "Meta"),

    // ─────────────────────────────
    // DeepSeek
    // ─────────────────────────────
    ("deepseek-ai/deepseek-r1", "DeepSeek R1", "DeepSeek"),
    ("deepseek-ai/deepseek-v3.1", "DeepSeek V3.1", "DeepSeek"),
    ("deepseek-ai/deepseek-v3", "DeepSeek V3", "DeepSeek"),

    // ─────────────────────────────
    // Qwen
    // ─────────────────────────────
    ("qwen/qwen3-235b-a22b-instruct-2507", "Qwen3 235B Instruct", "Qwen"),

    // ─────────────────────────────
    // IBM Granite
    // ─────────────────────────────
    ("ibm-granite/granite-4.0-h-small", "Granite 4.0 H Small", "IBM"),
    ("ibm-granite/granite-3.3-8b-instruct", "Granite 3.3 8B Instruct", "IBM"),
    ("ibm-granite/granite-3.2-8b-instruct", "Granite 3.2 8B Instruct", "IBM"),
    ("ibm-granite/granite-3.1-8b-instruct", "Granite 3.1 8B Instruct", "IBM"),
    ("ibm-granite/granite-3.1-2b-instruct", "Granite 3.1 2B Instruct", "IBM"),

    // ─────────────────────────────
    // Snowflake
    // ─────────────────────────────
    ("snowflake/snowflake-arctic-instruct", "Snowflake Arctic Instruct", "Snowflake"),

    // ─────────────────────────────
    // Moonshot AI
    // ─────────────────────────────
    ("moonshotai/kimi-k2-thinking", "Kimi K2 Thinking", "Moonshot AI"),

    // ─────────────────────────────
    // Perceptron
    // ─────────────────────────────
    ("perceptron-ai-inc/isaac-0.1", "Isaac 0.1", "Perceptron AI"),
];

}

