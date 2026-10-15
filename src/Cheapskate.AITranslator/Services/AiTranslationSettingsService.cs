using EPiServer.Data.Dynamic;
using Cheapskate.AITranslator.Models;

namespace Cheapskate.AITranslator.Services;

public sealed class AiTranslationSettingsService : IAiTranslationSettingsService
{
    private const string StoreName = "Cheapskate.AITranslator.Settings";
    private const string RetiredGroqModel = "llama-3.1-8b-instant";
    private readonly object _sync = new();

    public AiTranslationSettings Get()
    {
        lock (_sync)
        {
            var store = GetStore();
            var settings = store.LoadAll<AiTranslationSettings>().FirstOrDefault() ?? new AiTranslationSettings();
            if (settings.Provider == AiTranslationProvider.GroqCloud
                && string.Equals(settings.Model, RetiredGroqModel, StringComparison.Ordinal))
            {
                settings.Model = AiTranslationSettings.GroqDefaultModel;
                SaveCore(settings);
            }

            return settings;
        }
    }

    public void Save(AiTranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            SaveCore(settings);
        }
    }

    private static void SaveCore(AiTranslationSettings settings)
    {
        var factory = DynamicDataStoreFactory.Instance;
        factory.DeleteStore(StoreName, deleteObjects: true);
        factory.CreateStore(StoreName, typeof(AiTranslationSettings)).Save(settings);
    }

    private static DynamicDataStore GetStore()
    {
        return DynamicDataStoreFactory.Instance.GetStore(StoreName)
            ?? DynamicDataStoreFactory.Instance.CreateStore(StoreName, typeof(AiTranslationSettings));
    }
}
