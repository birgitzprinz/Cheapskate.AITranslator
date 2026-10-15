using Cheapskate.AITranslator.Models;

namespace Cheapskate.AITranslator.Services;

public interface IAiTranslationClient
{
    Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationWorkItem> batch,
        AiTranslationSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class AiTranslationClient : IAiTranslationClient
{
    private readonly IReadOnlyDictionary<AiTranslationProvider, IAiTranslationProviderClient> _providerClients;

    public AiTranslationClient(IEnumerable<IAiTranslationProviderClient> providerClients)
    {
        _providerClients = providerClients.ToDictionary(client => client.Provider);
    }

    public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationWorkItem> batch,
        AiTranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        if (!_providerClients.TryGetValue(settings.Provider, out var providerClient))
            throw new InvalidOperationException($"No translation client is registered for provider '{settings.Provider}'.");

        return providerClient.TranslateAsync(batch, settings, cancellationToken);
    }

}
