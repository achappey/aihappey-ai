using AIHappey.Core.Providers.Alibaba;

namespace AIHappey.Tests.Alibaba;

public class AlibabaProviderModelsTests
{
    [Theory]
    [InlineData("model", "image", "IG")]
    [InlineData("model", "video", "VG")]
    [InlineData("model", "video", "3D-generation")]
    [InlineData("model", "transcription", "ASR")]
    [InlineData("model", "transcription", "Realtime-Audio-Translate")]
    [InlineData("model", "speech", "TTS")]
    [InlineData("model", "speech", "Realtime-Chatting")]
    [InlineData("model", "embedding", "TR")]
    [InlineData("model", "embedding", "ME")]
    [InlineData("model", "language", "TG")]
    [InlineData("model", "language", "Reasoning")]
    [InlineData("model", "language", "VU")]
    [InlineData("model", "language", "Realtime-Omni")]
    public void GetModelType_maps_documented_capabilities(
        string modelId,
        string expected,
        string capability)
    {
        Assert.Equal(expected, AlibabaProvider.GetModelTypeForTests(modelId, capability));
    }

    [Fact]
    public void GetModelType_prioritizes_generation_modality_over_text_capabilities()
    {
        Assert.Equal(
            "image",
            AlibabaProvider.GetModelTypeForTests("qwen-multimodal", "TG", "IG", "Reasoning"));
    }

    [Theory]
    [InlineData("custom-image-generator", "image")]
    [InlineData("custom-reranker", "reranking")]
    [InlineData("custom-model", "language")]
    public void GetModelType_falls_back_to_guess_model_type(string modelId, string expected)
    {
        Assert.Equal(expected, AlibabaProvider.GetModelTypeForTests(modelId));
        Assert.Equal(expected, AlibabaProvider.GetModelTypeForTests(modelId, "unrecognized-capability"));
    }
}
