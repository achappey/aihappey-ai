using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsUnifiedMapperRequestTests
{
    private const string GoogleReasoningFixturePath = "Fixtures/api-chat/raw/interactions-with-encrypted-content-chatrequest.json";

    [Fact]
    public void Vercel_chat_request_with_google_signature_only_reasoning_round_trips_to_interactions_request_with_thought_payload_preserved()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(GoogleReasoningFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{GoogleReasoningFixturePath}](Core/AIHappey.Tests/{GoogleReasoningFixturePath}).");

        var expectedSignatures = LoadThoughtSignatures(json, "google");
        var interactionRequest = chatRequest.ToUnifiedRequest("google").ToInteractionRequest("google");

        var thoughtParts = Assert.IsAssignableFrom<IReadOnlyList<InteractionThoughtContent>>(
            interactionRequest.Input?.Turns
                ?.SelectMany(turn => turn.Content?.Parts ?? [])
                .OfType<InteractionThoughtContent>()
                .ToList());

        Assert.Equal(expectedSignatures, [.. thoughtParts.Select(part => part.Signature!)]);
        Assert.All(thoughtParts, thought =>
        {
            Assert.True(thought.Summary is null or { Count: 0 });

            var encryptedContent = Assert.Contains("encrypted_content", thought.AdditionalProperties ?? []);
            Assert.Equal(thought.Signature, encryptedContent.GetString());

            var serialized = JsonSerializer.Serialize(thought, InteractionJson.Default);
            Assert.Contains("\"signature\"", serialized, StringComparison.Ordinal);
            Assert.Contains("\"encrypted_content\"", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("\"summary\"", serialized, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Signature_only_reasoning_is_not_rehydrated_when_provider_key_does_not_match()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(GoogleReasoningFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{GoogleReasoningFixturePath}](Core/AIHappey.Tests/{GoogleReasoningFixturePath}).");

        var interactionRequest = chatRequest.ToUnifiedRequest("google").ToInteractionRequest("xai");

        var thoughtParts = interactionRequest.Input?.Turns
            ?.SelectMany(turn => turn.Content?.Parts ?? [])
            .OfType<InteractionThoughtContent>()
            .ToList();

        Assert.Empty(thoughtParts ?? []);

        var serialized = JsonSerializer.Serialize(interactionRequest, InteractionJson.Default);
        Assert.DoesNotContain("thought_signature", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("encrypted_content", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Adjacent_signature_only_reasoning_parts_merge_without_losing_encrypted_content()
    {
        var request = new AIRequest
        {
            Model = "google/gemini-flash-lite-latest",
            ProviderId = "google",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            CreateSignatureOnlyReasoningPart("sig-123"),
                            CreateSignatureOnlyReasoningPart("sig-123")
                        ]
                    }
                ]
            }
        };

        var interactionRequest = request.ToInteractionRequest("google");
        var turn = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<InteractionTurn>>(interactionRequest.Input?.Turns));
        var thought = Assert.IsType<InteractionThoughtContent>(Assert.Single(turn.Content?.Parts ?? []));

        Assert.Equal("sig-123", thought.Signature);
        Assert.True(thought.Summary is null or { Count: 0 });
        Assert.Equal("sig-123", Assert.Contains("encrypted_content", thought.AdditionalProperties ?? []).GetString());
    }

    private static AIReasoningContentPart CreateSignatureOnlyReasoningPart(string signature)
        => new()
        {
            Type = "reasoning",
            Text = string.Empty,
            Metadata = new Dictionary<string, object?>
            {
                ["google"] = new Dictionary<string, object?>
                {
                    ["type"] = "thought_signature",
                    ["signature"] = signature,
                    ["encrypted_content"] = signature
                }
            }
        };

    private static List<string> LoadThoughtSignatures(string json, string providerId)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Where(message => message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .SelectMany(message => message.GetProperty("parts").EnumerateArray())
            .Where(part => part.TryGetProperty("type", out var type)
                && type.GetString() == "reasoning"
                && part.TryGetProperty("providerMetadata", out var providerMetadata)
                && providerMetadata.TryGetProperty(providerId, out var scopedMetadata)
                && scopedMetadata.TryGetProperty("signature", out _))
            .Select(part => part.GetProperty("providerMetadata").GetProperty(providerId).GetProperty("signature").GetString())
            .Where(static signature => !string.IsNullOrWhiteSpace(signature))
            .Select(static signature => signature!)
            .ToList();
    }
}
