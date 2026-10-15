using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Cheapskate.AITranslator.Models;
using Cheapskate.AITranslator.Services;

namespace Cheapskate.AITranslator.Controllers;

[Authorize(Roles = "CmsAdmins,Administrators")]
[Route("cheapskate/ai-translator/settings")]
public sealed class AiTranslationSettingsController : Controller
{
    private readonly IAiTranslationSettingsService _settings;
    private readonly ILogger<AiTranslationSettingsController> _logger;

    public AiTranslationSettingsController(
        IAiTranslationSettingsService settings,
        ILogger<AiTranslationSettingsController> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var settings = _settings.Get();
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            settings.ApiKey = AiTranslationSettings.MaskedApiKey;

        return View(settings);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Save(AiTranslationSettings settings)
    {
        var existingSettings = _settings.Get();
        if (settings.ApiKey == AiTranslationSettings.MaskedApiKey)
            settings.ApiKey = existingSettings.ApiKey;

        settings.ApiKey = settings.ApiKey?.Trim() ?? string.Empty;
        settings.AzureRegion = settings.AzureRegion?.Trim() ?? string.Empty;
        settings.ApplyProviderDefaults(existingSettings.Provider == settings.Provider);
        _logger.LogInformation(
            "Saving AI translation settings: provider {Provider}, API key configured {ApiKeyConfigured}, Azure region configured {RegionConfigured}",
            settings.Provider,
            !string.IsNullOrWhiteSpace(settings.ApiKey),
            !string.IsNullOrWhiteSpace(settings.AzureRegion));
        _settings.Save(settings);
        var savedSettings = _settings.Get();
        _logger.LogInformation(
            "AI translation settings saved: provider {Provider}, Azure region configured {RegionConfigured}",
            savedSettings.Provider,
            !string.IsNullOrWhiteSpace(savedSettings.AzureRegion));
        return RedirectToAction(nameof(Index));
    }
}
