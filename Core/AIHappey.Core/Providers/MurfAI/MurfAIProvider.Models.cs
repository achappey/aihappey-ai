using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider : IModelProvider
{

    public static IReadOnlyList<Model> MurfAIModels =>
    [
        new()
        {
            Id = "gen2".ToModelId("murfai"),
            Name = "gen2",
            Description = "Murf Speech Gen 2 is our most advanced, realistic, and customizable speech model. It challenges the limits of technology, by merging human-like realism with advanced customization capabilities, empowering users to efficiently bridge the gap between their vision and its execution",
            Type = "speech",
            OwnedBy = "MurfAI"
        },
        // Translation models (target languages)
    new() { Id = "translate/en-US".ToModelId("murfai"), Name = "English (US & Canada)", Description = "Translate to English (US & Canada).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/en-UK".ToModelId("murfai"), Name = "English (UK)", Description = "Translate to English (UK).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/en-IN".ToModelId("murfai"), Name = "English (India)", Description = "Translate to English (India).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/en-AU".ToModelId("murfai"), Name = "English (Australia)", Description = "Translate to English (Australia).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/en-SCOTT".ToModelId("murfai"), Name = "English (Scotland)", Description = "Translate to English (Scotland).", Type = "language", OwnedBy = "MurfAI" },

    new() { Id = "translate/es-MX".ToModelId("murfai"), Name = "Spanish (Mexico)", Description = "Translate to Spanish (Mexico).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/es-ES".ToModelId("murfai"), Name = "Spanish (Spain)", Description = "Translate to Spanish (Spain).", Type = "language", OwnedBy = "MurfAI" },

    new() { Id = "translate/fr-FR".ToModelId("murfai"), Name = "French (France)", Description = "Translate to French (France).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/de-DE".ToModelId("murfai"), Name = "German (Germany)", Description = "Translate to German (Germany).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/it-IT".ToModelId("murfai"), Name = "Italian (Italy)", Description = "Translate to Italian (Italy).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/nl-NL".ToModelId("murfai"), Name = "Dutch (Netherlands)", Description = "Translate to Dutch (Netherlands).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/pt-BR".ToModelId("murfai"), Name = "Portuguese (Brazil)", Description = "Translate to Portuguese (Brazil).", Type = "language", OwnedBy = "MurfAI" },

    new() { Id = "translate/zh-CN".ToModelId("murfai"), Name = "Chinese (Mandarin, China)", Description = "Translate to Chinese (Mandarin, China).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/ja-JP".ToModelId("murfai"), Name = "Japanese (Japan)", Description = "Translate to Japanese (Japan).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/ko-KR".ToModelId("murfai"), Name = "Korean (Korea)", Description = "Translate to Korean (Korea).", Type = "language", OwnedBy = "MurfAI" },

    new() { Id = "translate/hi-IN".ToModelId("murfai"), Name = "Hindi (India)", Description = "Translate to Hindi (India).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/ta-IN".ToModelId("murfai"), Name = "Tamil (India)", Description = "Translate to Tamil (India).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/bn-IN".ToModelId("murfai"), Name = "Bangla (India)", Description = "Translate to Bangla (India).", Type = "language", OwnedBy = "MurfAI" },

    new() { Id = "translate/hr-HR".ToModelId("murfai"), Name = "Croatian (Croatia)", Description = "Translate to Croatian (Croatia).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/sk-SK".ToModelId("murfai"), Name = "Slovak (Slovakia)", Description = "Translate to Slovak (Slovakia).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/pl-PL".ToModelId("murfai"), Name = "Polish (Poland)", Description = "Translate to Polish (Poland).", Type = "language", OwnedBy = "MurfAI" },
    new() { Id = "translate/el-GR".ToModelId("murfai"), Name = "Greek (Greece)", Description = "Translate to Greek (Greece).", Type = "language", OwnedBy = "MurfAI" }
    ];
}

