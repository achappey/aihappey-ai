using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Pollinations;

public static class PollinationsMessageMappingExtensions
{
    private static readonly JsonSerializerOptions J = JsonSerializerOptions.Web;

    /// <summary>
    /// Maps your internal UI messages to Together/OpenAI "messages" array.
    /// - user/system: content as array of parts (text, image_url)
    /// - assistant: streams parts in order; flushes a content array up to a ToolInvocationPart,
    ///              then emits an assistant message with tool_calls; if tool output present,
    ///              appends a separate role:"tool" message with tool_call_id + serialized content.
    /// </summary>
    public static IEnumerable<object> ToPollinationMessages(this IEnumerable<UIMessage> uiMessages)
    {
        foreach (var msg in uiMessages)
        {
            switch (msg.Role)
            {
                case Role.system:
                    {
                        var parts = msg.Parts.ToPollinationContentParts().OfType<string>().ToList();
                        foreach (var part in parts)
                        {
                            yield return new { role = "system", content = part };
                        }
                        break;
                    }

                case Role.user:
                    {
                        var parts = msg.Parts.ToPollinationContentParts().OfType<string>().ToList();
                        foreach (var part in parts)
                        {
                            yield return new { role = "user", content = part };
                        }
                        break;
                    }

                case Role.assistant:
                    {
                        // Buffer assistant content parts until a tool call appears
                        var buffer = new List<string>();

                        foreach (var part in msg.Parts)
                        {
                            switch (part)
                            {
                                // -------- text/image parts → keep order as content array --------
                                case TextUIPart:
                                    {
                                        var mapped = MapPartToPollinationContent(part);
                                        if (mapped is not null)
                                            buffer.Add(mapped);
                                        break;
                                    }

                                    // -------- tool invocation → (a) flush assistant content, (b) emit tool_calls, (c) optional tool result --------
                                    /*  case ToolInvocationPart tip:
                                          {
                                              // (a) flush pending assistant content (if any)
                                              if (buffer.Count > 0)
                                              {
                                                  yield return new { role = "assistant", content = buffer.ToArray() };
                                                  buffer.Clear();
                                              }

                                              // (b) emit assistant message with tool_calls
                                              var toolName = tip.GetToolName();
                                              var argsJson = tip.Input is null ? "{}" : JsonSerializer.Serialize(tip.Input, J);

                                              var toolCall = new
                                              {
                                                  id = tip.ToolCallId,
                                                  type = "function",
                                                  function = new { name = toolName, arguments = argsJson }
                                              };

                                              yield return new
                                              {
                                                  role = "assistant",
                                                  content = (object?)null, // no content when using tool_calls
                                                  tool_calls = new[] { toolCall }
                                              };

                                              // (c) if we already have the tool result, append a role:"tool" message
                                              if (tip.Output is not null)
                                              {
                                                  var toolContent = tip.Output is string s
                                                      ? s
                                                      : JsonSerializer.Serialize(tip.Output, J);

                                                  yield return new
                                                  {
                                                      role = "tool",
                                                      tool_call_id = tip.ToolCallId,
                                                      name = toolName,
                                                      content = toolContent // string per OpenAI/Together contract
                                                  };
                                              }

                                              break;
                                          }*/

                                    // Optional: map reasoning parts if you want; Together doesn't have a native type
                                    // case ReasoningUIPart r: // ignore or turn into text
                            }
                        }

                        foreach (var part in buffer)
                        {
                            yield return new { role = "assistant", content = part };
                        }

                        // trailing assistant content (no tools)
                        // if (buffer.Count > 0)
                        //    yield return new { role = "assistant", content = buffer.ToArray() };

                        break;
                    }

                default:
                    throw new NotSupportedException($"Unsupported UIMessage role: {msg.Role}");
            }
        }
    }

    /// <summary>
    /// Maps a UIMessage's parts into Together/OpenAI content parts (array).
    /// Currently supports:
    /// - TextUIPart → { type:"text", text }
    /// - FileUIPart (image/*) → { type:"image_url", image_url:{ url } }
    /// Extend here for other modalities if Together model supports them.
    /// </summary>
    public static IEnumerable<string?> ToPollinationContentParts(this IEnumerable<UIMessagePart> parts)
    {
        foreach (var p in parts)
        {
            var mapped = MapPartToPollinationContent(p);
            if (mapped is not null)
                yield return mapped;
        }
    }

    private static string? MapPartToPollinationContent(UIMessagePart part)
    {
        switch (part)
        {
            case TextUIPart t when !string.IsNullOrWhiteSpace(t.Text):
                return t.Text;

            /*      case FileUIPart f
                      when !string.IsNullOrEmpty(f.Url) &&
                           !string.IsNullOrEmpty(f.MediaType) &&
                           f.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                      return new
                      {
                          type = "image_url",
                          image_url = new { url = f.Url }
                      };*/

            // You can extend: PDFs to text, audio to input_audio, etc., when the target model supports it.
            default:
                return null;
        }
    }



}
