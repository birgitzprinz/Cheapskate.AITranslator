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
        "Translate only natural-language prose. Do not translate URLs, file paths, CSS classes, identifiers, numbers, code, markup, placeholders, or other technical tokens. " +
        "Preserve HTML markup, URLs, file paths, CSS classes, identifiers, numbers, and placeholders exactly. " +
        "Only text and rich text content should be translated; values that represent selections, enums, or other structured data must remain unchanged.";

    public static string BuildChatCompletionPrompt(string language, TranslationTone tone, string serializedEntries) =>
        ChatCompletionInstruction + " Target language: " + language + ". Tone: " + tone + ".\n" + serializedEntries;
}
