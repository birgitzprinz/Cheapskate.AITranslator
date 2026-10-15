using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
using Microsoft.Extensions.DependencyInjection;
using Cheapskate.AITranslator.Services;

namespace Cheapskate.AITranslator.Infrastructure.Episerver;

[InitializableModule]
[ModuleDependency(typeof(EPiServer.Shell.UI.InitializationModule))]
[ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
public class LanguageVersionCreatorInitialization : IConfigurableModule
{
    public void ConfigureContainer(ServiceConfigurationContext context)
    {
        context.Services.AddCheapskateAiTranslator();
    }

    public void Initialize(InitializationEngine context) { }

    public void Uninitialize(InitializationEngine context) { }
}
