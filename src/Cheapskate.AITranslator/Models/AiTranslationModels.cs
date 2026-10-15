namespace Cheapskate.AITranslator.Models;

public enum AiTranslationProvider
{
    GeminiFlashLite,
    AzureTranslator,
    DeepLFree,
    GroqCloud,
    Other
}

public enum TranslationTone
{
    Neutral,
    Humorous,
    Sarcastic,
    Formal,
    Academic,
    Casual,
    Business
}

public sealed class AiTranslationSettings
{
    public const string MaskedApiKey = "********";
    public const string GroqDefaultModel = "openai/gpt-oss-20b";

    public bool Enabled { get; set; }
    public AiTranslationProvider Provider { get; set; } = AiTranslationProvider.GeminiFlashLite;
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public string ApiKey { get; set; } = string.Empty;
    public string AzureRegion { get; set; } = string.Empty;
    public string Model { get; set; } = "gemini-3.5-flash-lite";
    public int MaxInputTokens { get; set; } = 8192;
    public int MaxOutputTokens { get; set; } = 2048;
    public bool ConserveApiCalls { get; set; } = true;

    public void ApplyProviderDefaults(bool preserveModel = false)
    {
        switch (Provider)
        {
            case AiTranslationProvider.GeminiFlashLite:
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                if (!preserveModel || string.IsNullOrWhiteSpace(Model))
                    Model = "gemini-3.5-flash-lite";
                break;
            case AiTranslationProvider.AzureTranslator:
                Endpoint = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";
                Model = string.Empty;
                break;
            case AiTranslationProvider.DeepLFree:
                Endpoint = "https://api-free.deepl.com/v2/translate";
                Model = string.Empty;
                break;
            case AiTranslationProvider.GroqCloud:
                Endpoint = "https://api.groq.com/openai/v1/chat/completions";
                if (!preserveModel || string.IsNullOrWhiteSpace(Model))
                    Model = GroqDefaultModel;
                break;
        }
    }
}

public sealed record TranslationWorkItem(
    string JobId,
    EPiServer.Core.ContentReference ContentLink,
    string Language,
    string PropertyName,
    string SourceText,
    TranslationTone Tone = TranslationTone.Neutral);

public enum TranslationJobState
{
    Queued,
    Running,
    Completed,
    Failed
}

public sealed class TranslationJobStatus
{
    public string JobId { get; init; } = string.Empty;
    public TranslationJobState State { get; set; }
    public int Total { get; set; }
    public int Completed { get; set; }
    public string? Error { get; set; }
}
