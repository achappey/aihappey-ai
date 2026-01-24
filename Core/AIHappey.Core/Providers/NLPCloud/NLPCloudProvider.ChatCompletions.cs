using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var messages = options.Messages
            .Select(m => (m.Role, ExtractText(m.Content)))
            .ToList();
        var kind = GetModelKind(options.Model, out var baseModel);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (kind)
        {
            case NLPCloudModelKind.Paraphrasing:
            {
                var text = BuildParaphrasingInput(messages);
                var paraphrased = await SendParaphrasingAsync(baseModel, text, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = paraphrased },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            case NLPCloudModelKind.Summarization:
            {
                var text = BuildSummarizationInput(messages);
                var summary = await SendSummarizationAsync(baseModel, text, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = summary },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            case NLPCloudModelKind.IntentClassification:
            {
                var text = BuildIntentClassificationInput(messages);
                var intent = await SendIntentClassificationAsync(baseModel, text, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = intent },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            case NLPCloudModelKind.CodeGeneration:
            {
                var instruction = BuildCodeGenerationInput(messages);
                var code = await SendCodeGenerationAsync(baseModel, instruction, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = code },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            case NLPCloudModelKind.GrammarSpellingCorrection:
            {
                var text = BuildGrammarSpellingCorrectionInput(messages);
                var correction = await SendGrammarSpellingCorrectionAsync(baseModel, text, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = correction },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            case NLPCloudModelKind.KeywordsKeyphrasesExtraction:
            {
                var text = BuildKeywordsKeyphrasesExtractionInput(messages);
                var keywords = await SendKeywordsKeyphrasesExtractionAsync(baseModel, text, cancellationToken).ConfigureAwait(false);
                var keywordsText = string.Join(", ", keywords);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = keywordsText },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            case NLPCloudModelKind.Translation:
            {
                var text = BuildTranslationInput(messages);
                var targetLanguage = GetTranslationTargetLanguageFromModel(options.Model);
                var translated = await SendTranslationAsync(baseModel, text, targetLanguage, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = translated },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
            default:
            {
                var payload = BuildChatbotRequest(
                    options.Model,
                    messages,
                    stream: false);

                var result = await SendChatbotAsync(options.Model, payload, cancellationToken).ConfigureAwait(false);

                return new ChatCompletion
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = result.Response },
                            finish_reason = "stop"
                        }
                    ],
                    Usage = null
                };
            }
        }
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messages = options.Messages
            .Select(m => (m.Role, ExtractText(m.Content)))
            .ToList();
        var kind = GetModelKind(options.Model, out var baseModel);

        switch (kind)
        {
            case NLPCloudModelKind.Paraphrasing:
            {
                var text = BuildParaphrasingInput(messages);

                var paraphraseId = Guid.NewGuid().ToString("n");
                var paraphraseCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = paraphraseId,
                    Created = paraphraseCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamParaphrasingAsync(baseModel, text, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = paraphraseId,
                        Created = paraphraseCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = paraphraseId,
                    Created = paraphraseCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            case NLPCloudModelKind.Summarization:
            {
                var text = BuildSummarizationInput(messages);

                var summaryId = Guid.NewGuid().ToString("n");
                var summaryCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = summaryId,
                    Created = summaryCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamSummarizationAsync(baseModel, text, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = summaryId,
                        Created = summaryCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = summaryId,
                    Created = summaryCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            case NLPCloudModelKind.IntentClassification:
            {
                var text = BuildIntentClassificationInput(messages);

                var intentId = Guid.NewGuid().ToString("n");
                var intentCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = intentId,
                    Created = intentCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamIntentClassificationAsync(baseModel, text, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = intentId,
                        Created = intentCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = intentId,
                    Created = intentCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            case NLPCloudModelKind.CodeGeneration:
            {
                var instruction = BuildCodeGenerationInput(messages);

                var codeId = Guid.NewGuid().ToString("n");
                var codeCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = codeId,
                    Created = codeCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamCodeGenerationAsync(baseModel, instruction, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = codeId,
                        Created = codeCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = codeId,
                    Created = codeCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            case NLPCloudModelKind.GrammarSpellingCorrection:
            {
                var text = BuildGrammarSpellingCorrectionInput(messages);

                var correctionId = Guid.NewGuid().ToString("n");
                var correctionCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = correctionId,
                    Created = correctionCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamGrammarSpellingCorrectionAsync(baseModel, text, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = correctionId,
                        Created = correctionCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = correctionId,
                    Created = correctionCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            case NLPCloudModelKind.KeywordsKeyphrasesExtraction:
            {
                var text = BuildKeywordsKeyphrasesExtractionInput(messages);

                var keywordsId = Guid.NewGuid().ToString("n");
                var keywordsCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = keywordsId,
                    Created = keywordsCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var keyword in StreamKeywordsKeyphrasesExtractionAsync(baseModel, text, cancellationToken))
                {
                    if (string.IsNullOrEmpty(keyword))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = keywordsId,
                        Created = keywordsCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = keyword }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = keywordsId,
                    Created = keywordsCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            case NLPCloudModelKind.Translation:
            {
                var text = BuildTranslationInput(messages);
                var targetLanguage = GetTranslationTargetLanguageFromModel(options.Model);

                var translationId = Guid.NewGuid().ToString("n");
                var translationCreated = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = translationId,
                    Created = translationCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamTranslationAsync(baseModel, text, targetLanguage, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = translationId,
                        Created = translationCreated,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = translationId,
                    Created = translationCreated,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                yield break;
            }
            default:
            {
                var payload = BuildChatbotRequest(
                    options.Model,
                    messages,
                    stream: true);

                var id = Guid.NewGuid().ToString("n");
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                yield return new ChatCompletionUpdate
                {
                    Id = id,
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
                    ]
                };

                await foreach (var chunk in StreamChatbotAsync(options.Model, payload, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    yield return new ChatCompletionUpdate
                    {
                        Id = id,
                        Created = created,
                        Model = options.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                        ]
                    };
                }

                yield return new ChatCompletionUpdate
                {
                    Id = id,
                    Created = created,
                    Model = options.Model,
                    Choices =
                    [
                        new { index = 0, delta = new { }, finish_reason = "stop" }
                    ]
                };
                break;
            }
        }
    }

}
