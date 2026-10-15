namespace Cheapskate.AITranslator.Services;

using Cheapskate.AITranslator.Models;

/// <summary>
/// Prompt templates used by the translation providers.
/// Keep provider prompt wording here so it can be adjusted without searching service logic.
/// </summary>
public static class TranslationPrompts
{
    public const string ChatCompletionInstruction =
        "Translate each text value into the target language. Return only a JSON array of objects with exactly two fields: the unchanged id and translated. Never return text, source, or any other field. " +
        "Translate all natural-language prose, including every visible text node inside HTML or rich text. " +
        "For HTML values, return the complete translated HTML value with the same tags, nesting, order, attributes, entities, URLs, file paths, CSS classes, identifiers, numbers, code, and placeholders unchanged. " +
        "Never translate or remove tag names, attribute names, attribute values, URLs, technical tokens, or markup. Never summarize, shorten, omit, or truncate any text or HTML. " +
        "Only visible natural-language text should change; values that represent selections, enums, or other structured data must remain unchanged.";

    public static string BuildChatCompletionPrompt(string language, TranslationTone tone, string serializedEntries) =>
        ChatCompletionInstruction + " Target language: " + language + ". Tone: " + tone + ".\n" + serializedEntries;
}
