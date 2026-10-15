using EPiServer.Shell.Modules;
using Microsoft.Extensions.DependencyInjection;
using Cheapskate.AITranslator.Services;

namespace Cheapskate.AITranslator.Infrastructure.Episerver;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCheapskateAiTranslator(this IServiceCollection services)
    {
        services.Configure<ProtectedModuleOptions>(options =>
        {
            if (!options.Items.Any(item => item.Name.Equals("Cheapskate.AITranslator", StringComparison.OrdinalIgnoreCase)))
                options.Items.Add(new ModuleDetails { Name = "Cheapskate.AITranslator", Assemblies = { "Cheapskate.AITranslator" } });
        });
        services.AddScoped<ILanguageVersionCreationService, LanguageVersionCreationService>();
        services.AddSingleton<IAiTranslationSettingsService, AiTranslationSettingsService>();
        services.AddSingleton<IAiTranslationProviderClient, GeminiTranslationProviderClient>();
        services.AddSingleton<IAiTranslationProviderClient, AzureTranslationProviderClient>();
        services.AddSingleton<IAiTranslationProviderClient, DeepLTranslationProviderClient>();
        services.AddSingleton<IAiTranslationProviderClient, GroqTranslationProviderClient>();
        services.AddSingleton<IAiTranslationProviderClient, OtherTranslationProviderClient>();
        services.AddSingleton<IAiTranslationClient, AiTranslationClient>();
        services.AddSingleton<AiTranslationJobQueue>();
        services.AddSingleton<ITranslationJobQueue>(provider => provider.GetRequiredService<AiTranslationJobQueue>());
        services.AddHostedService(provider => provider.GetRequiredService<AiTranslationJobQueue>());
        services.AddHttpClient();
        return services;
    }
}