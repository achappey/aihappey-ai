using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var model = options.Model ?? throw new ArgumentException("Model is required.", nameof(options));

        var messages = new List<(string Role, string? Content)>();
        if (options.Input?.IsText == true)
        {
            messages.Add(("user", options.Input.Text));
        }
        else if (options.Input?.Items is not null)
        {
            foreach (var item in options.Input.Items.OfType<ResponseInputMessage>())
            {
                var role = item.Role.ToString().ToLowerInvariant();
                var messageText = ExtractText(item.Content);
                messages.Add((role, messageText));
            }
        }

        var kind = GetModelKind(model, out var baseModel);
        var now = DateTimeOffset.UtcNow;

        switch (kind)
        {
            case NLPCloudModelKind.Paraphrasing:
            {
                var text = BuildParaphrasingInput(messages);
                var paraphrased = await SendParaphrasingAsync(baseModel, text, cancellationToken);
                var itemIdParaphrase = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdParaphrase,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = paraphrased, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
            case NLPCloudModelKind.Summarization:
            {
                var text = BuildSummarizationInput(messages);
                var summary = await SendSummarizationAsync(baseModel, text, cancellationToken);
                var itemIdSummary = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdSummary,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = summary, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
            case NLPCloudModelKind.IntentClassification:
            {
                var text = BuildIntentClassificationInput(messages);
                var intent = await SendIntentClassificationAsync(baseModel, text, cancellationToken);
                var itemIdIntent = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdIntent,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = intent, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
            case NLPCloudModelKind.CodeGeneration:
            {
                var instruction = BuildCodeGenerationInput(messages);
                var code = await SendCodeGenerationAsync(baseModel, instruction, cancellationToken);
                var itemIdCode = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdCode,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = code, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
            case NLPCloudModelKind.GrammarSpellingCorrection:
            {
                var text = BuildGrammarSpellingCorrectionInput(messages);
                var correction = await SendGrammarSpellingCorrectionAsync(baseModel, text, cancellationToken);
                var itemIdCorrection = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdCorrection,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = correction, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
            case NLPCloudModelKind.KeywordsKeyphrasesExtraction:
            {
                var text = BuildKeywordsKeyphrasesExtractionInput(messages);
                var keywords = await SendKeywordsKeyphrasesExtractionAsync(baseModel, text, cancellationToken);
                var itemIdKeywords = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdKeywords,
                            status = "completed",
                            role = "assistant",
                            content = keywords
                                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                                .Select(keyword => new { type = "output_text", text = keyword, annotations = Array.Empty<string>() })
                                .ToArray()
                        }
                    ]
                };
            }
            case NLPCloudModelKind.Translation:
            {
                var text = BuildTranslationInput(messages);
                var targetLanguage = GetTranslationTargetLanguageFromModel(model);
                var translated = await SendTranslationAsync(baseModel, text, targetLanguage, cancellationToken);
                var itemIdTranslation = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemIdTranslation,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = translated, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
            default:
            {
                var payload = BuildChatbotRequest(
                    model,
                    messages,
                    stream: false);

                var result = await SendChatbotAsync(model, payload, cancellationToken);
                var itemId = Guid.NewGuid().ToString("n");

                return new ResponseResult
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Model = model,
                    CreatedAt = now.ToUnixTimeSeconds(),
                    CompletedAt = now.ToUnixTimeSeconds(),
                    Output =
                    [
                        new
                        {
                            type = "message",
                            id = itemId,
                            status = "completed",
                            role = "assistant",
                            content = new[]
                            {
                                new { type = "output_text", text = result.Response, annotations = Array.Empty<string>() }
                            }
                        }
                    ]
                };
            }
        }
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = options.Model ?? throw new ArgumentException("Model is required.", nameof(options));
        var messages = new List<(string Role, string? Content)>();

        if (options.Input?.IsText == true)
        {
            messages.Add(("user", options.Input.Text));
        }
        else if (options.Input?.Items is not null)
        {
            foreach (var item in options.Input.Items.OfType<ResponseInputMessage>())
            {
                var role = item.Role.ToString().ToLowerInvariant();
                var messageText = ExtractText(item.Content);
                messages.Add((role, messageText));
            }
        }

        var kind = GetModelKind(model, out var baseModel);
        switch (kind)
        {
            case NLPCloudModelKind.Paraphrasing:
            {
                var paraphraseText = BuildParaphrasingInput(messages);
                var paraphraseResponseId = Guid.NewGuid().ToString("n");
                var paraphraseCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var paraphraseSeq = 0;

                var paraphraseBaseResponse = new ResponseResult
                {
                    Id = paraphraseResponseId,
                    Model = model,
                    CreatedAt = paraphraseCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = paraphraseSeq++,
                    Response = paraphraseBaseResponse
                };

                var paraphraseItemId = Guid.NewGuid().ToString("n");
                var paraphraseFull = new StringBuilder();

                await foreach (var chunk in StreamParaphrasingAsync(baseModel, paraphraseText, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    paraphraseFull.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = paraphraseSeq++,
                        ItemId = paraphraseItemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var resultText = paraphraseFull.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = paraphraseSeq++,
                    ItemId = paraphraseItemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = resultText
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = paraphraseSeq++,
                    Response = new ResponseResult
                    {
                        Id = paraphraseResponseId,
                        Model = model,
                        CreatedAt = paraphraseCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = paraphraseItemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = resultText, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                yield break;
            }
            case NLPCloudModelKind.Summarization:
            {
                var summaryText = BuildSummarizationInput(messages);
                var summaryResponseId = Guid.NewGuid().ToString("n");
                var summaryCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var summarySeq = 0;

                var summaryBaseResponse = new ResponseResult
                {
                    Id = summaryResponseId,
                    Model = model,
                    CreatedAt = summaryCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = summarySeq++,
                    Response = summaryBaseResponse
                };

                var summaryItemId = Guid.NewGuid().ToString("n");
                var summaryFull = new StringBuilder();

                await foreach (var chunk in StreamSummarizationAsync(baseModel, summaryText, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    summaryFull.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = summarySeq++,
                        ItemId = summaryItemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var resultText = summaryFull.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = summarySeq++,
                    ItemId = summaryItemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = resultText
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = summarySeq++,
                    Response = new ResponseResult
                    {
                        Id = summaryResponseId,
                        Model = model,
                        CreatedAt = summaryCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = summaryItemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = resultText, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                yield break;
            }
            case NLPCloudModelKind.IntentClassification:
            {
                var intentText = BuildIntentClassificationInput(messages);
                var intentResponseId = Guid.NewGuid().ToString("n");
                var intentCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var intentSeq = 0;

                var intentBaseResponse = new ResponseResult
                {
                    Id = intentResponseId,
                    Model = model,
                    CreatedAt = intentCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = intentSeq++,
                    Response = intentBaseResponse
                };

                var intentItemId = Guid.NewGuid().ToString("n");
                var intentFull = new StringBuilder();

                await foreach (var chunk in StreamIntentClassificationAsync(baseModel, intentText, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    intentFull.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = intentSeq++,
                        ItemId = intentItemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var intentResultText = intentFull.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = intentSeq++,
                    ItemId = intentItemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = intentResultText
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = intentSeq++,
                    Response = new ResponseResult
                    {
                        Id = intentResponseId,
                        Model = model,
                        CreatedAt = intentCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = intentItemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = intentResultText, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                yield break;
            }
            case NLPCloudModelKind.CodeGeneration:
            {
                var codeText = BuildCodeGenerationInput(messages);
                var codeResponseId = Guid.NewGuid().ToString("n");
                var codeCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var codeSeq = 0;

                var codeBaseResponse = new ResponseResult
                {
                    Id = codeResponseId,
                    Model = model,
                    CreatedAt = codeCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = codeSeq++,
                    Response = codeBaseResponse
                };

                var codeItemId = Guid.NewGuid().ToString("n");
                var codeFull = new StringBuilder();

                await foreach (var chunk in StreamCodeGenerationAsync(baseModel, codeText, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    codeFull.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = codeSeq++,
                        ItemId = codeItemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var codeResultText = codeFull.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = codeSeq++,
                    ItemId = codeItemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = codeResultText
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = codeSeq++,
                    Response = new ResponseResult
                    {
                        Id = codeResponseId,
                        Model = model,
                        CreatedAt = codeCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = codeItemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = codeResultText, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                yield break;
            }
            case NLPCloudModelKind.GrammarSpellingCorrection:
            {
                var correctionText = BuildGrammarSpellingCorrectionInput(messages);
                var correctionResponseId = Guid.NewGuid().ToString("n");
                var correctionCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var correctionSeq = 0;

                var correctionBaseResponse = new ResponseResult
                {
                    Id = correctionResponseId,
                    Model = model,
                    CreatedAt = correctionCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = correctionSeq++,
                    Response = correctionBaseResponse
                };

                var correctionItemId = Guid.NewGuid().ToString("n");
                var correctionFull = new StringBuilder();

                await foreach (var chunk in StreamGrammarSpellingCorrectionAsync(baseModel, correctionText, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    correctionFull.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = correctionSeq++,
                        ItemId = correctionItemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var correctionResultText = correctionFull.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = correctionSeq++,
                    ItemId = correctionItemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = correctionResultText
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = correctionSeq++,
                    Response = new ResponseResult
                    {
                        Id = correctionResponseId,
                        Model = model,
                        CreatedAt = correctionCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = correctionItemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = correctionResultText, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                yield break;
            }
            case NLPCloudModelKind.KeywordsKeyphrasesExtraction:
            {
                var keywordsText = BuildKeywordsKeyphrasesExtractionInput(messages);
                var keywordsResponseId = Guid.NewGuid().ToString("n");
                var keywordsCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var keywordsSeq = 0;

                var keywordsBaseResponse = new ResponseResult
                {
                    Id = keywordsResponseId,
                    Model = model,
                    CreatedAt = keywordsCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = keywordsSeq++,
                    Response = keywordsBaseResponse
                };

                var keywordsItemId = Guid.NewGuid().ToString("n");
                var keywordParts = new List<string>();
                var contentIndex = 0;

                await foreach (var keyword in StreamKeywordsKeyphrasesExtractionAsync(baseModel, keywordsText, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                        continue;

                    keywordParts.Add(keyword);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = keywordsSeq++,
                        ItemId = keywordsItemId,
                        ContentIndex = contentIndex,
                        Outputindex = 0,
                        Delta = keyword
                    };

                    yield return new ResponseOutputTextDone
                    {
                        SequenceNumber = keywordsSeq++,
                        ItemId = keywordsItemId,
                        ContentIndex = contentIndex,
                        Outputindex = 0,
                        Text = keyword
                    };

                    contentIndex++;
                }

                yield return new ResponseCompleted
                {
                    SequenceNumber = keywordsSeq++,
                    Response = new ResponseResult
                    {
                        Id = keywordsResponseId,
                        Model = model,
                        CreatedAt = keywordsCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = keywordsItemId,
                                status = "completed",
                                role = "assistant",
                                content = keywordParts
                                    .Select(part => new { type = "output_text", text = part, annotations = Array.Empty<string>() })
                                    .ToArray()
                            }
                        ]
                    }
                };
                yield break;
            }
            case NLPCloudModelKind.Translation:
            {
                var translationText = BuildTranslationInput(messages);
                var targetLanguage = GetTranslationTargetLanguageFromModel(model);
                var translationResponseId = Guid.NewGuid().ToString("n");
                var translationCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var translationSeq = 0;

                var translationBaseResponse = new ResponseResult
                {
                    Id = translationResponseId,
                    Model = model,
                    CreatedAt = translationCreatedAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = translationSeq++,
                    Response = translationBaseResponse
                };

                var translationItemId = Guid.NewGuid().ToString("n");
                var translationFull = new StringBuilder();

                await foreach (var chunk in StreamTranslationAsync(baseModel, translationText, targetLanguage, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    translationFull.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = translationSeq++,
                        ItemId = translationItemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var translationResultText = translationFull.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = translationSeq++,
                    ItemId = translationItemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = translationResultText
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = translationSeq++,
                    Response = new ResponseResult
                    {
                        Id = translationResponseId,
                        Model = model,
                        CreatedAt = translationCreatedAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = translationItemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text = translationResultText, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                yield break;
            }
            default:
            {
                var payload = BuildChatbotRequest(
                    model,
                    messages,
                    stream: true);

                var responseId = Guid.NewGuid().ToString("n");
                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var seq = 0;

                var baseResponse = new ResponseResult
                {
                    Id = responseId,
                    Model = model,
                    CreatedAt = createdAt,
                    Output = []
                };

                yield return new ResponseCreated
                {
                    SequenceNumber = seq++,
                    Response = baseResponse
                };

                var itemId = Guid.NewGuid().ToString("n");
                var full = new StringBuilder();

                await foreach (var chunk in StreamChatbotAsync(model, payload, cancellationToken))
                {
                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    full.Append(chunk);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = seq++,
                        ItemId = itemId,
                        ContentIndex = 0,
                        Outputindex = 0,
                        Delta = chunk
                    };
                }

                var text = full.ToString();
                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = seq++,
                    ItemId = itemId,
                    ContentIndex = 0,
                    Outputindex = 0,
                    Text = text
                };

                yield return new ResponseCompleted
                {
                    SequenceNumber = seq++,
                    Response = new ResponseResult
                    {
                        Id = responseId,
                        Model = model,
                        CreatedAt = createdAt,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Output =
                        [
                            new
                            {
                                type = "message",
                                id = itemId,
                                status = "completed",
                                role = "assistant",
                                content = new[]
                                {
                                    new { type = "output_text", text, annotations = Array.Empty<string>() }
                                }
                            }
                        ]
                    }
                };
                break;
            }
        }
    }
}
