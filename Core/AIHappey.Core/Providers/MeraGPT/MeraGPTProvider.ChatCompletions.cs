using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.MeraGPT;

public sealed partial class MeraGPTProvider
{
    private async Task<ChatCompletion> CompleteChatAsyncCore(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        var model = NormalizeModel(options.Model);
        if (model is "decider-1" or "restyler-1")
            throw new NotSupportedException($"MeraGPT model '{model}' does not support ChatCompletions.");
        options.Model = model;
        ValidateFanoutOptions(options, model);

        ApplyAuthHeader();
        var completion = await this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
        if (model == "fanout-1")
            ValidateFanoutResponse(completion);
        return completion;
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsyncCore(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        var model = NormalizeModel(options.Model);
        if (model is "decider-1" or "restyler-1")
            throw new NotSupportedException($"MeraGPT model '{model}' does not support ChatCompletions.");
        if (model == "fanout-1")
            throw new NotSupportedException("MeraGPT Fanout does not support streaming.");

        options.Model = model;
        ApplyAuthHeader();
        await foreach (var update in this.GetChatCompletions(
            _client,
            options,
            cancellationToken: cancellationToken))
            yield return update;
    }

    private static void ValidateFanoutOptions(ChatCompletionOptions options, string model)
    {
        if (model != "fanout-1")
            return;

        if ((options.Temperature.HasValue && options.Temperature.Value != 1f)
            || (options.Tools is { } tools && tools.Any())
            || options.ToolChoice is not null
                && !options.ToolChoice.Equals("none", StringComparison.OrdinalIgnoreCase)
            || options.ResponseFormat is not null
            || options.ParallelToolCalls is not null
            || options.Stream == true
            || options.AdditionalProperties?.Keys.Any(name =>
                name is "brands" or "category" or "top_p" or "max_tokens" or "max_completion_tokens") == true)
            throw new NotSupportedException(
                "MeraGPT Fanout fixes sampling and output limits; tools, structured output, brands, category, and sampling overrides are unsupported through ChatCompletions.");
    }

    private static void ValidateFanoutResponse(ChatCompletion completion)
    {
        if (completion.AdditionalProperties?.TryGetValue("meragpt", out var metadata) != true
            || metadata.ValueKind != System.Text.Json.JsonValueKind.Object
            || !metadata.TryGetProperty("queries", out var queries)
            || queries.ValueKind != System.Text.Json.JsonValueKind.Array
            || queries.GetArrayLength() > 6
            || queries.EnumerateArray().Any(query => query.ValueKind != System.Text.Json.JsonValueKind.String))
            throw new InvalidOperationException("MeraGPT Fanout returned an invalid structured queries response.");
    }

    private static string NormalizeModel(string? model)
    {
        const string prefix = "meragpt/";
        return model?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? model[prefix.Length..]
            : model ?? string.Empty;
    }
}
