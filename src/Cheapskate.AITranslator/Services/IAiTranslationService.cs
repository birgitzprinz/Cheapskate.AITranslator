using Cheapskate.AITranslator.Models;

namespace Cheapskate.AITranslator.Services;

public interface IAiTranslationSettingsService
{
    AiTranslationSettings Get();
    void Save(AiTranslationSettings settings);
}

public interface ITranslationJobQueue
{
    string Enqueue(IReadOnlyCollection<TranslationWorkItem> items);
    TranslationJobStatus? GetStatus(string jobId);
}
