using EPiServer.Shell.Navigation;
using EPiServer.Framework.Localization;

namespace Cheapskate.AITranslator.Infrastructure.Episerver;

[MenuProvider]
public sealed class AiTranslationMenuProvider : IMenuProvider
{
    public IEnumerable<MenuItem> GetMenuItems()
    {
        yield return new UrlMenuItem(
            LocalizationService.Current.GetString("/plugins/cheapskateaitranslator/displayname"),
            MenuPaths.Global + "/cms/cheapskate-ai-translator-settings",
            "/cheapskate/ai-translator/settings")
        {
            IsAvailable = context => true,
            SortIndex = SortIndex.Last
        };
    }
}
