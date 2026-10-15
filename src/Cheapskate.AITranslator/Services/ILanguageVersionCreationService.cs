using EPiServer.Core;
using Cheapskate.AITranslator.Models;

namespace Cheapskate.AITranslator.Services;

public interface ILanguageVersionCreationService
{
    bool IsContentLocalizable(ContentReference contentLink);
    ContentLanguageOptionsResult GetLanguageOptions(ContentReference contentLink);
    Task<LanguageVersionCreationResult> CreateLanguageVersionsAsync(LanguageVersionCreationRequest request, CancellationToken cancellationToken = default);
}
