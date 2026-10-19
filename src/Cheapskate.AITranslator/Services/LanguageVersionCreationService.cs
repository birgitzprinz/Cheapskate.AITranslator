using System.Collections;
using System.Globalization;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using EPiServer.SpecializedProperties;
using EPiServer.VisualBuilder;
using Microsoft.Extensions.Logging;
using Cheapskate.AITranslator.Models;

namespace Cheapskate.AITranslator.Services;

public class LanguageVersionCreationService : ILanguageVersionCreationService
{
    private readonly IContentRepository _contentRepository;
    private readonly IContentTypeRepository _contentTypeRepository;
    private readonly IContentVersionRepository _contentVersionRepository;
    private readonly ILanguageBranchRepository _languageBranchRepository;
    private readonly IAiTranslationSettingsService _translationSettings;
    private readonly ITranslationJobQueue _translationJobs;
    private readonly ILogger<LanguageVersionCreationService> _logger;

    public LanguageVersionCreationService(
        IContentRepository contentRepository,
        IContentTypeRepository contentTypeRepository,
        IContentVersionRepository contentVersionRepository,
        ILanguageBranchRepository languageBranchRepository,
        ILogger<LanguageVersionCreationService> logger,
        IAiTranslationSettingsService translationSettings,
        ITranslationJobQueue translationJobs)
    {
        _contentRepository = contentRepository ?? throw new ArgumentNullException(nameof(contentRepository));
        _contentTypeRepository = contentTypeRepository ?? throw new ArgumentNullException(nameof(contentTypeRepository));
        _contentVersionRepository = contentVersionRepository ?? throw new ArgumentNullException(nameof(contentVersionRepository));
        _languageBranchRepository = languageBranchRepository ?? throw new ArgumentNullException(nameof(languageBranchRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _translationSettings = translationSettings ?? throw new ArgumentNullException(nameof(translationSettings));
        _translationJobs = translationJobs ?? throw new ArgumentNullException(nameof(translationJobs));
    }

    // Cheap, single-lookup check used by the CMS toolbar command to decide whether to enable
    // itself. Deliberately avoids the heavier GetLanguageOptions (language branches + enabled
    // languages enumeration) so the button reacts instantly to selection changes.
    public bool IsContentLocalizable(ContentReference contentLink)
    {
        if (ContentReference.IsNullOrEmpty(contentLink))
            throw new ArgumentNullException(nameof(contentLink));

        var content = _contentRepository.Get<IContent>(contentLink);
        return content is ILocalizable && !IsSysRoot(content);
    }

    public ContentLanguageOptionsResult GetLanguageOptions(ContentReference contentLink)
    {
        if (ContentReference.IsNullOrEmpty(contentLink))
            throw new ArgumentNullException(nameof(contentLink));

        var rootContent = _contentRepository.Get<IContent>(contentLink);
        if (rootContent is not ILocalizable || IsSysRoot(rootContent))
            throw new InvalidOperationException("Language versions can only be created for localizable content.");

        var masterCulture = GetMasterCulture(rootContent);

        var existingBranches = _contentRepository.GetLanguageBranches<IContent>(contentLink)
            .Select(GetContentCulture)
            .Where(c => c != null)
            .Select(c => c!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var enabledLanguages = _languageBranchRepository.ListEnabled().ToList();

        var languageOptions = enabledLanguages.Select(lb => new LanguageOptionDto
        {
            LanguageId = lb.LanguageID,
            Name = lb.Name,
            DisplayName = lb.Culture.DisplayName,
            ExistsOnContent = existingBranches.Contains(lb.LanguageID),
            IsMasterLanguage = lb.LanguageID.Equals(masterCulture.Name, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        var aiSettings = _translationSettings.Get();
        return new ContentLanguageOptionsResult
        {
            ContentLink = contentLink,
            ContentName = rootContent.Name,
            MasterLanguage = masterCulture.Name,
            Languages = languageOptions,
            AiTranslationConfigured = aiSettings.Enabled
                && !string.IsNullOrWhiteSpace(aiSettings.Endpoint)
                && !string.IsNullOrWhiteSpace(aiSettings.ApiKey)
        };
    }

    public Task<LanguageVersionCreationResult> CreateLanguageVersionsAsync(
        LanguageVersionCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (ContentReference.IsNullOrEmpty(request.ContentLink))
            throw new ArgumentException("ContentLink cannot be null or empty", nameof(request));

        var result = new LanguageVersionCreationResult();
        var rootContent = GetMasterContentForCloning(request.ContentLink);
        if (rootContent is not ILocalizable || IsSysRoot(rootContent))
            throw new InvalidOperationException("Language versions can only be created for localizable content.");

        var masterCulture = GetMasterCulture(rootContent);

        var targetCultures = ResolveTargetCultures(request, masterCulture);
        if (targetCultures.Count == 0)
        {
            _logger.LogInformation("No target languages to create for content {ContentLink}", request.ContentLink);
            return Task.FromResult(result);
        }

        foreach (var targetCulture in targetCultures)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var visitedGuids = new HashSet<Guid>();
            var translationItems = new List<TranslationWorkItem>();
            ProcessNode(rootContent, targetCulture, visitedGuids, result, translationItems, request.IncludeLinkedContents, request.UseAiTranslation, request.Tone);
            if (request.UseAiTranslation && translationItems.Count > 0)
                result.TranslationJobIds.Add(_translationJobs.Enqueue(translationItems));
        }

        return Task.FromResult(result);
    }

    private void ProcessNode(
        IContent content,
        CultureInfo targetCulture,
        HashSet<Guid> visitedGuids,
        LanguageVersionCreationResult result,
        List<TranslationWorkItem> translationItems,
        bool includeLinkedContents,
        bool useAiTranslation,
        TranslationTone tone)
    {
        if (content == null)
        {
            _logger.LogInformation("Language version node skipped: content is null for target language {Language}", targetCulture.Name);
            return;
        }

        if (content is not ILocalizable)
        {
            _logger.LogInformation(
                "Language version node skipped: content {ContentLink} ({ContentType}) is not localizable for target language {Language}",
                content.ContentLink,
                content.GetType().FullName,
                targetCulture.Name);
            return;
        }

        if (!visitedGuids.Add(content.ContentGuid))
        {
            // Already visited in this language run -> avoids infinite loops and duplicate creations
            _logger.LogInformation(
                "Language version node skipped: content {ContentLink} already visited for target language {Language}",
                content.ContentLink,
                targetCulture.Name);
            return;
        }

        var contentType = _contentTypeRepository.Load(content.ContentTypeID);
        var contentTypeName = contentType?.Name ?? content.GetType().Name;

        // Check if language branch already exists
        var existingBranches = _contentRepository.GetLanguageBranches<IContent>(content.ContentLink);
        var branchExists = existingBranches.Any(b =>
        {
            var culture = GetContentCulture(b);
            return culture != null && culture.Name.Equals(targetCulture.Name, StringComparison.OrdinalIgnoreCase);
        });

        if (branchExists)
        {
            result.Items.Add(new LanguageVersionItemResult
            {
                ContentLink = content.ContentLink,
                Name = content.Name,
                Language = targetCulture.Name,
                ContentType = contentTypeName,
                Status = LanguageCreationStatus.SkippedAlreadyExists,
                Reason = $"Language branch '{targetCulture.Name}' already exists"
            });
        }
        else
        {
            try
            {
                // Create draft language branch (CheckedOut status, not published)
                var createdBranch = _contentRepository.CreateLanguageBranch<IContent>(
                    content.ContentLink,
                    targetCulture);
                var newBranch = CreateWritableContentClone(createdBranch);
                MarkNonLanguageSpecificPropertiesAsLanguageSpecific(newBranch);

                // Copy the master values before the initial draft save. Translation jobs run later
                // and obtain a separate writable clone before updating individual properties.
                if (string.IsNullOrEmpty(newBranch.Name) && !string.IsNullOrEmpty(content.Name))
                    newBranch.Name = content.Name;
                var nodeTranslationItems = new List<TranslationWorkItem>();
                CopyLanguageSpecificProperties(content, newBranch, contentType, nodeTranslationItems, targetCulture, useAiTranslation, tone);
                _contentRepository.Save(newBranch, SaveAction.Save, AccessLevel.NoAccess);
                translationItems.AddRange(nodeTranslationItems);

                result.Items.Add(new LanguageVersionItemResult
                {
                    ContentLink = content.ContentLink,
                    Name = newBranch.Name,
                    Language = targetCulture.Name,
                    ContentType = contentTypeName,
                    Status = LanguageCreationStatus.CreatedDraft
                });

                _logger.LogInformation(
                    "Created draft language branch '{Language}' for {ContentType} '{Name}' ({ContentLink})",
                    targetCulture.Name,
                    contentTypeName,
                    newBranch.Name,
                    content.ContentLink);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to create language branch '{Language}' for {ContentType} '{Name}' ({ContentLink})",
                    targetCulture.Name,
                    contentTypeName,
                    content.Name,
                    content.ContentLink);

                result.Items.Add(new LanguageVersionItemResult
                {
                    ContentLink = content.ContentLink,
                    Name = content.Name,
                    Language = targetCulture.Name,
                    ContentType = contentTypeName,
                    Status = LanguageCreationStatus.Failed,
                    Reason = ex.Message
                });
            }
        }

        if (includeLinkedContents)
        {
            // Recursively traverse ContentAreas to find linked child and shared content.
            TraverseContentAreas(content, targetCulture, visitedGuids, result, translationItems, includeLinkedContents, useAiTranslation, tone);
        }
    }

    private void CopyLanguageSpecificProperties(
        IContent masterContent,
        IContent targetContent,
        ContentType? contentType,
        List<TranslationWorkItem> translationItems,
        CultureInfo targetCulture,
        bool useAiTranslation,
        TranslationTone tone)
    {
        if (contentType == null || masterContent.Property == null || targetContent.Property == null)
            return;

        // Copy Name if target name is empty or needs initializing
        if (string.IsNullOrEmpty(targetContent.Name) && !string.IsNullOrEmpty(masterContent.Name))
        {
            targetContent.Name = masterContent.Name;
        }

        foreach (var targetProp in targetContent.Property.Where(property =>
            property.IsLanguageSpecific
            && !property.IsMetaData
            && !property.IsReadOnly))
        {
            var masterProp = masterContent.Property[targetProp.Name];
            _logger.LogInformation(
                "AI translation property inspection: content type {ContentType}, runtime type {RuntimeType}, property {PropertyName}, language specific {LanguageSpecific}, metadata {Metadata}, read-only {ReadOnly}, property type {PropertyType}, CLR attributes {Attributes}",
                contentType.Name,
                masterContent.GetType().FullName,
                targetProp.Name,
                targetProp.IsLanguageSpecific,
                targetProp.IsMetaData,
                targetProp.IsReadOnly,
                targetProp.GetType().Name,
                GetPropertyAttributeNames(masterContent.GetType(), targetProp.Name));

            if (masterProp == null)
            {
                _logger.LogInformation(
                    "AI translation property skipped: {PropertyName}; master property missing",
                    targetProp.Name);
                continue;
            }

            if (masterProp.Value == null)
            {
                _logger.LogInformation("AI translation property skipped: {PropertyName}; value is null", targetProp.Name);
                continue;
            }

            targetProp.Value = CloneMasterPropertyValue(masterProp.Value);
            if (useAiTranslation
                && !IsNotTranslatable(targetProp)
                && !IsExcludedProperty(targetContent, targetProp))
            {
                QueueTranslatableValue(
                    masterProp.Value,
                    targetProp.Name,
                    targetContent.ContentLink,
                    targetCulture,
                    translationItems,
                    tone);
            }
            else
            {
                _logger.LogInformation("AI translation property not queued: {PropertyName}; value type {ValueType}", targetProp.Name, masterProp.Value.GetType().FullName);
            }
        }

        if (useAiTranslation
            && masterContent is ExperienceData experienceData
            && experienceData.UnstructuredData != null)
        {
            CollectContentAreaInlineBlockTranslations(
                experienceData.UnstructuredData,
                nameof(ExperienceData.UnstructuredData),
                targetContent.ContentLink,
                targetCulture,
                translationItems,
                tone);
        }
    }

    private static void MarkNonLanguageSpecificPropertiesAsLanguageSpecific(IContent content)
    {
        foreach (var property in content.Property)
        {
            if (!property.IsLanguageSpecific && !property.IsMetaData && !property.IsReadOnly)
                property.IsLanguageSpecific = true;
        }
    }

    private void CollectNestedBlockTranslations(
        object? value,
        string propertyPath,
        ContentReference contentLink,
        CultureInfo targetCulture,
        List<TranslationWorkItem> translationItems,
        TranslationTone tone)
    {
        if (value is ExperienceData experienceData && experienceData.UnstructuredData != null)
        {
            CollectContentAreaInlineBlockTranslations(
                experienceData.UnstructuredData,
                propertyPath + ".UnstructuredData",
                contentLink,
                targetCulture,
                translationItems,
                tone);
        }

        if (value is not BlockData block || block.Property == null)
            return;

        foreach (var property in block.Property)
        {
            var nestedValue = property.Value;
            var nestedPath = propertyPath + "." + property.Name;
            _logger.LogInformation(
                "AI translation nested property inspection: block type {BlockType}, property {PropertyPath}, not translatable {NotTranslatable}, CLR attributes {Attributes}, value type {ValueType}",
                block.GetType().FullName,
                nestedPath,
                IsNotTranslatable(property),
                GetPropertyAttributeNames(block.GetType(), property.Name),
                nestedValue?.GetType().FullName ?? "<null>");

            if (nestedValue == null)
            {
                _logger.LogInformation(
                    "AI translation nested property skipped: {PropertyPath}; reason {Reason}",
                    nestedPath,
                    "value is null");
                continue;
            }

            if (nestedValue is ContentArea)
            {
                CollectContentAreaInlineBlockTranslations(
                    (ContentArea)nestedValue,
                    nestedPath,
                    contentLink,
                    targetCulture,
                    translationItems,
                    tone);
                continue;
            }

            if (!IsNotTranslatable(property))
            {
                QueueTranslatableValue(
                    nestedValue,
                    nestedPath,
                    contentLink,
                    targetCulture,
                    translationItems,
                    tone);
            }
        }
    }

    private void QueueTranslatableValue(
        object? value,
        string propertyPath,
        ContentReference contentLink,
        CultureInfo targetCulture,
        List<TranslationWorkItem> translationItems,
        TranslationTone tone)
    {
        if (value is LinkItem linkItem)
        {
            QueueTranslatableValue(linkItem.Text, propertyPath + ".Text", contentLink, targetCulture, translationItems, tone);
            QueueTranslatableValue(linkItem.Title, propertyPath + ".Title", contentLink, targetCulture, translationItems, tone);
            return;
        }

        if (value is ContentArea contentArea)
        {
            CollectContentAreaInlineBlockTranslations(
                contentArea,
                propertyPath,
                contentLink,
                targetCulture,
                translationItems,
                tone);
            return;
        }

        if (TryGetTranslatableText(value, out var sourceText) && !IsUrl(sourceText))
        {
            if (!translationItems.Any(item =>
                    item.ContentLink == contentLink
                    && string.Equals(item.Language, targetCulture.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.PropertyName, propertyPath, StringComparison.Ordinal)
                    && string.Equals(item.SourceText, sourceText, StringComparison.Ordinal)))
            {
                translationItems.Add(new TranslationWorkItem(
                    string.Empty,
                    contentLink,
                    targetCulture.Name,
                    propertyPath,
                    sourceText,
                    tone));
                _logger.LogInformation("AI translation property queued: {PropertyPath}", propertyPath);
            }
        }

        if (value is IEnumerable enumerable && value is not string && value is not XhtmlString)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                QueueTranslatableValue(
                    item,
                    $"{propertyPath}[{index}]",
                    contentLink,
                    targetCulture,
                    translationItems,
                    tone);
                index++;
            }
        }

        CollectNestedBlockTranslations(value, propertyPath, contentLink, targetCulture, translationItems, tone);
    }

    private void CollectContentAreaInlineBlockTranslations(
        ContentArea contentArea,
        string propertyPath,
        ContentReference contentLink,
        CultureInfo targetCulture,
        List<TranslationWorkItem> translationItems,
        TranslationTone tone)
    {
        if (contentArea.Items == null)
            return;

        for (var index = 0; index < contentArea.Items.Count; index++)
        {
            var item = contentArea.Items[index];
            if (item.InlineBlock is not BlockData inlineBlock)
                continue;

            var inlineBlockPath = $"{propertyPath}[{index}].InlineBlock";
            _logger.LogInformation(
                "AI translation inline block discovered: {PropertyPath}; block type {BlockType}",
                inlineBlockPath,
                inlineBlock.GetType().FullName);

            CollectNestedBlockTranslations(
                inlineBlock,
                inlineBlockPath,
                contentLink,
                targetCulture,
                translationItems,
                tone);
        }
    }

    // Complex property types (XhtmlString, ContentArea, LinkItemCollection, etc.) are often
    // returned as read-only instances; Clone() alone preserves that read-only state, so
    // assigning it back throws "instance is read-only" later. Prefer CreateWritableClone()
    // (via EPiServer.Core.IReadOnly<T>, resolved by duck-typing since T varies per property).
    private static object CloneMasterPropertyValue(object value)
    {
        var type = value.GetType();

        if (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(System.Collections.ObjectModel.ReadOnlyCollection<>))
        {
            var elementType = type.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var writableValues = (System.Collections.IList)Activator.CreateInstance(listType)!;

            foreach (var item in (System.Collections.IEnumerable)value)
                writableValues.Add(item == null ? null : CloneMasterPropertyValue(item));

            return writableValues;
        }

        var isReadOnlyProperty = type.GetProperty("IsReadOnly");
        var createWritableCloneMethod = type.GetMethod("CreateWritableClone", Type.EmptyTypes);

        if (isReadOnlyProperty != null && createWritableCloneMethod != null
            && isReadOnlyProperty.GetValue(value) is true)
        {
            return createWritableCloneMethod.Invoke(value, null)!;
        }

        return value is ICloneable cloneable ? cloneable.Clone() : value;
    }

    private static IContent CreateWritableContentClone(IContent content)
    {
        var method = content.GetType().GetMethod("CreateWritableClone", Type.EmptyTypes)
            ?? throw new InvalidOperationException($"Content type '{content.GetType().FullName}' does not support writable cloning.");
        return method.Invoke(content, null) as IContent
            ?? throw new InvalidOperationException($"Content type '{content.GetType().FullName}' returned no writable clone.");
    }

    // Clones must always come from the master language's PUBLISHED version, never from a
    // pending common draft, so translators start from approved content. Falls back to the
    // latest master-language draft only when nothing has been published yet.
    private IContent GetMasterContentForCloning(ContentReference contentLink)
    {
        var anyVersion = _contentRepository.Get<IContent>(contentLink, LanguageSelector.AutoDetect());
        return GetMasterContentForCloning(contentLink, anyVersion);
    }

    private IContent GetMasterContentForCloning(ContentReference contentLink, string masterLanguageName)
    {
        var anyVersion = _contentRepository.Get<IContent>(contentLink, new LanguageSelector(masterLanguageName));
        return GetMasterContentForCloning(contentLink, anyVersion);
    }

    private IContent GetMasterContentForCloning(ContentReference contentLink, IContent anyVersion)
    {
        if (anyVersion is not ILocalizable localizable || localizable.MasterLanguage == null)
            return anyVersion;

        var masterLanguageName = localizable.MasterLanguage.Name;
        var publishedVersion = _contentVersionRepository.LoadPublished(contentLink, masterLanguageName);
        if (publishedVersion != null)
        {
            return _contentRepository.Get<IContent>(publishedVersion.ContentLink);
        }

        return _contentRepository.Get<IContent>(contentLink, new LanguageSelector(masterLanguageName));
    }

    private void TraverseContentAreas(
        IContent content,
        CultureInfo targetCulture,
        HashSet<Guid> visitedGuids,
        LanguageVersionCreationResult result,
        List<TranslationWorkItem> translationItems,
        bool includeLinkedContents,
        bool useAiTranslation,
        TranslationTone tone)
    {
        if (content.Property == null)
            return;

        foreach (var property in content.Property)
        {
            if (property.Value is ContentArea contentArea && contentArea.Items != null)
            {
                TraverseLinkedContentArea(
                    contentArea,
                    $"{content.GetType().Name}.{property.Name}",
                    content,
                    targetCulture,
                    visitedGuids,
                    result,
                    translationItems,
                    includeLinkedContents,
                    useAiTranslation,
                    tone);
            }

            if (property.Value is BlockData block)
            {
                TraverseNestedBlockContentAreas(
                    block,
                    $"{content.GetType().Name}.{property.Name}",
                    content,
                    targetCulture,
                    visitedGuids,
                    result,
                    translationItems,
                    includeLinkedContents,
                    useAiTranslation,
                    tone);
            }
        }
    }

    private void TraverseNestedBlockContentAreas(
        BlockData block,
        string propertyPath,
        IContent ownerContent,
        CultureInfo targetCulture,
        HashSet<Guid> visitedGuids,
        LanguageVersionCreationResult result,
        List<TranslationWorkItem> translationItems,
        bool includeLinkedContents,
        bool useAiTranslation,
        TranslationTone tone)
    {
        if (block.Property == null)
            return;

        foreach (var property in block.Property)
        {
            var nestedPath = propertyPath + "." + property.Name;
            if (property.Value is ContentArea contentArea)
            {
                TraverseLinkedContentArea(
                    contentArea,
                    nestedPath,
                    ownerContent,
                    targetCulture,
                    visitedGuids,
                    result,
                    translationItems,
                    includeLinkedContents,
                    useAiTranslation,
                    tone);
            }
            else if (property.Value is BlockData nestedBlock)
            {
                TraverseNestedBlockContentAreas(
                    nestedBlock,
                    nestedPath,
                    ownerContent,
                    targetCulture,
                    visitedGuids,
                    result,
                    translationItems,
                    includeLinkedContents,
                    useAiTranslation,
                    tone);
            }
        }
    }

    private void TraverseLinkedContentArea(
        ContentArea contentArea,
        string propertyPath,
        IContent ownerContent,
        CultureInfo targetCulture,
        HashSet<Guid> visitedGuids,
        LanguageVersionCreationResult result,
        List<TranslationWorkItem> translationItems,
        bool includeLinkedContents,
        bool useAiTranslation,
        TranslationTone tone)
    {
        if (contentArea.Items == null)
            return;

        _logger.LogInformation(
            "Traversing content area on {ContentLink}, property {PropertyName}, item count {ItemCount}, target language {Language}",
            ownerContent.ContentLink,
            propertyPath,
            contentArea.Items.Count,
            targetCulture.Name);

        for (var itemIndex = 0; itemIndex < contentArea.Items.Count; itemIndex++)
        {
            var item = contentArea.Items[itemIndex];
            if (ContentReference.IsNullOrEmpty(item.ContentLink))
                continue;

            if (item.ContentGuid != Guid.Empty && visitedGuids.Contains(item.ContentGuid))
                continue;

            IContent? childContent = null;
            try
            {
                var masterLanguage = GetMasterCulture(ownerContent).Name;
                childContent = GetMasterContentForCloning(item.ContentLink, masterLanguage);
                _logger.LogInformation(
                    "Loaded content area child {ChildContentLink} as master language {MasterLanguage} for target language {TargetLanguage}",
                    item.ContentLink,
                    masterLanguage,
                    targetCulture.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load child content {ContentLink} for language version creation", item.ContentLink);
            }

            if (childContent != null)
            {
                ProcessNode(childContent, targetCulture, visitedGuids, result, translationItems, includeLinkedContents, useAiTranslation, tone);
            }
            else
            {
                _logger.LogInformation("Content area child {ChildContentLink} could not be loaded", item.ContentLink);
            }
        }
    }

    private List<CultureInfo> ResolveTargetCultures(LanguageVersionCreationRequest request, CultureInfo masterCulture)
    {
        var enabledLanguages = _languageBranchRepository.ListEnabled().ToList();

        if (request.AllLanguages || request.TargetLanguages == null || request.TargetLanguages.Count == 0)
        {
            return enabledLanguages
                .Where(lb => !lb.Culture.Name.Equals(masterCulture.Name, StringComparison.OrdinalIgnoreCase))
                .Select(lb => lb.Culture)
                .ToList();
        }

        var targetLanguageSet = request.TargetLanguages.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return enabledLanguages
            .Where(lb => targetLanguageSet.Contains(lb.LanguageID) || targetLanguageSet.Contains(lb.Culture.Name))
            .Where(lb => !lb.Culture.Name.Equals(masterCulture.Name, StringComparison.OrdinalIgnoreCase))
            .Select(lb => lb.Culture)
            .ToList();
    }

    private static CultureInfo GetMasterCulture(IContent content)
    {
        if (content is ILocalizable localizable && localizable.MasterLanguage != null)
        {
            return localizable.MasterLanguage;
        }

        return new CultureInfo("en");
    }

    private bool IsSysRoot(IContent content) =>
        string.Equals(
            _contentTypeRepository.Load(content.ContentTypeID)?.Name,
            "SysRoot",
            StringComparison.OrdinalIgnoreCase);

    private static CultureInfo? GetContentCulture(IContent content)
    {
        if (content is ILocalizable localizable)
        {
            return localizable.Language;
        }

        return null;
    }

    private static bool TryGetTranslatableText(object? value, out string text)
    {
        switch (value)
        {
            case string stringValue when !string.IsNullOrWhiteSpace(stringValue):
                text = stringValue;
                return true;
            case XhtmlString xhtml when !string.IsNullOrWhiteSpace(xhtml.ToHtmlString()):
                text = xhtml.ToHtmlString();
                return true;
            default:
                text = string.Empty;
                return false;
        }
    }

    private static bool IsNotTranslatable(PropertyData property) =>
        property is PropertyDropDownList
            || property is PropertyUrl
            || property is PropertyDate
            || property is PropertyGuid
            || property is PropertyAppSettings
            || property is PropertyAppSettingsMultiple
            || property is PropertyCheckBoxList
            || property is PropertyLanguage
            || property is PropertyTimeSpan
            || property is PropertyBlob
            || property is PropertySelector;

    private static bool IsExcludedProperty(IContent content, PropertyData property) =>
        string.Equals(content.ContentLink.ProviderName, "CatalogContent", StringComparison.OrdinalIgnoreCase)
        && string.Equals(property.Name, "SeoUri", StringComparison.OrdinalIgnoreCase);

    private static bool HasSelectionFactory(IContent content, PropertyDefinition propertyDefinition) =>
        HasSelectionFactory(content.GetType(), propertyDefinition.Name);

    private static bool HasSelectionFactory(Type contentType, string propertyName)
    {
        for (var type = contentType; type != null; type = type.BaseType)
        {
            if (HasSelectionAttribute(type.GetProperty(
                    propertyName,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.DeclaredOnly)))
            {
                return true;
            }
        }

        foreach (var interfaceType in contentType.GetInterfaces())
        {
            if (HasSelectionAttribute(interfaceType.GetProperty(propertyName)))
                return true;
        }

        return false;
    }

    private static bool HasSelectionAttribute(System.Reflection.PropertyInfo? property) =>
        property?.GetCustomAttributes(inherit: true)
            .Any(attribute =>
            {
                var attributeName = attribute.GetType().Name;
                return attributeName.Equals("SelectOneAttribute", StringComparison.Ordinal)
                    || attributeName.Equals("SelectManyAttribute", StringComparison.Ordinal);
            }) == true;

    private static string GetPropertyAttributeNames(Type contentType, string propertyName)
    {
        var attributes = new List<string>();
        for (var type = contentType; type != null; type = type.BaseType)
        {
            var property = type.GetProperty(
                propertyName,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (property != null)
            {
                attributes.AddRange(property.GetCustomAttributes(inherit: true).Select(attribute => attribute.GetType().FullName ?? attribute.GetType().Name));
            }
        }

        foreach (var interfaceType in contentType.GetInterfaces())
        {
            var property = interfaceType.GetProperty(propertyName);
            if (property != null)
            {
                attributes.AddRange(property.GetCustomAttributes(inherit: true).Select(attribute => attribute.GetType().FullName ?? attribute.GetType().Name));
            }
        }

        return attributes.Count == 0 ? "<none>" : string.Join(", ", attributes.Distinct(StringComparer.Ordinal));
    }

    private static bool IsUrl(string text)
    {
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("/", StringComparison.Ordinal)
            || trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }
}
