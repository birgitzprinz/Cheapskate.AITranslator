using System.Collections.Concurrent;
using System.Collections;
using System.Text.Json;
using System.Threading.Channels;
using EPiServer;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Core;
using EPiServer.Security;
using EPiServer.SpecializedProperties;
using EPiServer.VisualBuilder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cheapskate.AITranslator.Models;

namespace Cheapskate.AITranslator.Services;

public sealed class AiTranslationJobQueue : BackgroundService, ITranslationJobQueue
{
    private readonly Channel<TranslationJob> _queue = Channel.CreateUnbounded<TranslationJob>();
    private readonly ConcurrentDictionary<string, TranslationJobStatus> _statuses = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAiTranslationClient _translationClient;
    private readonly ILogger<AiTranslationJobQueue> _logger;

    public AiTranslationJobQueue(
        IServiceScopeFactory scopeFactory,
        IAiTranslationClient translationClient,
        ILogger<AiTranslationJobQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _translationClient = translationClient;
        _logger = logger;
    }

    public string Enqueue(IReadOnlyCollection<TranslationWorkItem> items)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var status = new TranslationJobStatus { JobId = jobId, State = TranslationJobState.Queued, Total = items.Count };
        _statuses[jobId] = status;
        _queue.Writer.TryWrite(new TranslationJob(jobId, items));
        return jobId;
    }

    public TranslationJobStatus? GetStatus(string jobId) =>
        _statuses.TryGetValue(jobId, out var status) ? status : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_statuses.TryGetValue(job.JobId, out var status))
                continue;

            status.State = TranslationJobState.Running;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<IAiTranslationSettingsService>().Get();
                if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.ApiKey))
                    throw new InvalidOperationException("AI translation is not configured.");

                await TranslateBatchesAsync(job.Items, settings, status, scope.ServiceProvider, stoppingToken);
                status.State = TranslationJobState.Completed;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status.State = TranslationJobState.Failed;
                status.Error = ex.Message;
                _logger.LogError(ex, "AI translation job {JobId} failed", job.JobId);
            }
        }
    }

    private async Task TranslateBatchesAsync(
        IReadOnlyCollection<TranslationWorkItem> items,
        AiTranslationSettings settings,
        TranslationJobStatus status,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var batches = BuildBatches(items, settings);
        foreach (var batch in batches)
        {
            _logger.LogInformation("AI translation batch started: {ItemCount} items", batch.Count);
            var translations = await _translationClient.TranslateAsync(batch, settings, cancellationToken);
            var repository = services.GetRequiredService<IContentRepository>();
            foreach (var contentBatch in batch.GroupBy(item => new { item.ContentLink, item.Language }))
            {
                var targetBranch = repository.Get<IContent>(
                    contentBatch.Key.ContentLink,
                    new LanguageSelector(contentBatch.Key.Language));
                targetBranch ??= repository.GetLanguageBranches<IContent>(contentBatch.Key.ContentLink)
                    .FirstOrDefault(branch => branch is ILocalizable localizable
                        && localizable.Language.Name.Equals(contentBatch.Key.Language, StringComparison.OrdinalIgnoreCase));

                if (targetBranch == null)
                {
                    _logger.LogWarning(
                        "AI translation result not applied: target language branch was not found for content {ContentLink}, language {Language}",
                        contentBatch.Key.ContentLink,
                        contentBatch.Key.Language);
                    continue;
                }

                var content = GetWritableClone(targetBranch);
                var appliedItems = new List<TranslationWorkItem>();
                try
                {
                    foreach (var item in contentBatch)
                    {
                        if (!translations.TryGetValue(item.PropertyName + "|" + item.ContentLink, out var translated))
                        {
                            _logger.LogInformation(
                                "AI translation result missing: content {ContentLink}, language {Language}, property {PropertyName}",
                                item.ContentLink,
                                item.Language,
                                item.PropertyName);
                            continue;
                        }

                        if (!TryApplyTranslation(content, item.PropertyName, translated))
                        {
                            _logger.LogInformation(
                                "AI translation result not applied: property path could not be resolved, {PropertyName}",
                                item.PropertyName);
                            continue;
                        }

                        appliedItems.Add(item);
                    }

                    if (appliedItems.Count == 0)
                        continue;

                    repository.Save(content, SaveAction.Save, AccessLevel.NoAccess);
                    status.Completed += appliedItems.Count;
                    foreach (var item in appliedItems)
                    {
                        _logger.LogInformation("AI translation result applied: content {ContentLink}, language {Language}, property {PropertyName}", item.ContentLink, item.Language, item.PropertyName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "AI translation result failed for content {ContentLink}, language {Language}; continuing with remaining content",
                        contentBatch.Key.ContentLink,
                        contentBatch.Key.Language);
                }
            }
        }
    }

    private static bool TryApplyTranslation(IContent content, string propertyPath, string translated)
    {
        if (propertyPath.Contains('.', StringComparison.Ordinal)
            || propertyPath.Contains('[', StringComparison.Ordinal))
        {
            return TrySetNestedValue(content, propertyPath, translated);
        }

        if (!TryGetProperty(content, propertyPath, out var property) || property.IsReadOnly)
            return false;

        property.Value = property.Value is XhtmlString ? new XhtmlString(translated) : translated;
        property.IsModified = true;
        return property.IsModified;
    }

    private static bool TryGetProperty(IContent content, string propertyPath, out PropertyData property)
    {
        property = null!;
        var pathParts = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length == 0)
            return false;

        object current = content;
        for (var index = 0; index < pathParts.Length; index++)
        {
            var pathPart = pathParts[index];
            var itemIndexStart = pathPart.IndexOf('[', StringComparison.Ordinal);
            var propertyName = itemIndexStart >= 0 ? pathPart[..itemIndexStart] : pathPart;
            var itemIndex = -1;
            if (itemIndexStart >= 0
                && pathPart.EndsWith("]", StringComparison.Ordinal)
                && int.TryParse(pathPart[(itemIndexStart + 1)..^1], out var parsedIndex))
            {
                itemIndex = parsedIndex;
            }

            if (current is ContentAreaItem contentAreaItem)
            {
                if (!propertyName.Equals(nameof(ContentAreaItem.InlineBlock), StringComparison.Ordinal)
                    || itemIndex >= 0
                    || contentAreaItem.InlineBlock == null)
                {
                    return false;
                }

                current = contentAreaItem.InlineBlock;
                continue;
            }

            if (current is ExperienceData experienceData
                && propertyName.Equals(nameof(ExperienceData.UnstructuredData), StringComparison.Ordinal))
            {
                var unstructuredData = experienceData.UnstructuredData;
                if (unstructuredData == null)
                    return false;

                current = unstructuredData;
                if (itemIndex >= 0)
                {
                    if (itemIndex >= unstructuredData.Items.Count)
                        return false;

                    current = unstructuredData.Items[itemIndex];
                }

                continue;
            }

            var properties = current switch
            {
                IContent contentValue => contentValue.Property,
                BlockData blockValue => blockValue.Property,
                _ => null
            };
            if (properties == null)
                return false;

            var currentProperty = properties[propertyName];
            if (currentProperty == null)
                return false;

            if (index == pathParts.Length - 1 && itemIndex < 0)
            {
                property = currentProperty;
                return true;
            }

            current = currentProperty.Value!;
            if (itemIndex >= 0)
            {
                if (!TryGetIndexedValue(current, itemIndex, out current))
                    return false;
            }
        }

        return false;
    }

    private static bool TrySetNestedValue(IContent content, string propertyPath, string translated)
    {
        var pathParts = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object current = content;
        PropertyData? rootProperty = null;
        for (var index = 0; index < pathParts.Length; index++)
        {
            var pathPart = pathParts[index];
            var itemIndexStart = pathPart.IndexOf('[', StringComparison.Ordinal);
            var propertyName = itemIndexStart >= 0 ? pathPart[..itemIndexStart] : pathPart;
            var itemIndex = ParseItemIndex(pathPart, itemIndexStart);
            var isLast = index == pathParts.Length - 1;

            if (current is ContentAreaItem contentAreaItem)
            {
                if (!propertyName.Equals(nameof(ContentAreaItem.InlineBlock), StringComparison.Ordinal)
                    || itemIndex >= 0
                    || contentAreaItem.InlineBlock == null)
                    return false;

                current = contentAreaItem.InlineBlock;
                continue;
            }

            if (current is ExperienceData experienceData
                && propertyName.Equals(nameof(ExperienceData.UnstructuredData), StringComparison.Ordinal))
            {
                if (experienceData.UnstructuredData == null)
                    return false;

                current = experienceData.UnstructuredData;
                if (itemIndex >= 0 && !TryGetIndexedValue(current, itemIndex, out current))
                    return false;
                continue;
            }

            if (itemIndex >= 0 && string.IsNullOrEmpty(propertyName))
            {
                if (isLast && current is IList indexedList && itemIndex < indexedList.Count)
                {
                    indexedList[itemIndex] = translated;
                    return MarkRootPropertyChanged(rootProperty);
                }

                if (!TryGetIndexedValue(current, itemIndex, out current))
                    return false;
                continue;
            }

            var properties = current switch
            {
                IContent contentValue => contentValue.Property,
                BlockData blockValue => blockValue.Property,
                _ => null
            };
            if (properties == null)
            {
                if (isLast && current is LinkItem linkItem)
                {
                    var linkProperty = typeof(LinkItem).GetProperty(propertyName);
                    if (linkProperty?.CanWrite != true)
                        return false;

                    linkProperty.SetValue(linkItem, translated);
                    return MarkRootPropertyChanged(rootProperty);
                }

                return false;
            }

            var currentProperty = properties[propertyName];
            if (currentProperty == null)
                return false;

            rootProperty ??= currentProperty;

            current = currentProperty.Value!;
            if (isLast && itemIndex < 0)
            {
                currentProperty.Value = currentProperty.Value is XhtmlString ? new XhtmlString(translated) : translated;
                currentProperty.IsModified = true;
                return MarkRootPropertyChanged(rootProperty);
            }

            if (isLast && itemIndex >= 0 && current is IList list && itemIndex < list.Count)
            {
                list[itemIndex] = translated;
                return MarkRootPropertyChanged(rootProperty);
            }

            if (itemIndex >= 0 && !TryGetIndexedValue(current, itemIndex, out current))
                return false;
        }

        return false;
    }

    private static bool MarkRootPropertyChanged(PropertyData? rootProperty)
    {
        if (rootProperty == null)
            return false;

        rootProperty.IsModified = true;
        return rootProperty.IsModified;
    }

    private static bool TryGetIndexedValue(object? value, int index, out object current)
    {
        current = null!;
        if (value is ContentArea contentArea)
        {
            if (index < 0 || index >= contentArea.Items.Count)
                return false;

            current = contentArea.Items[index];
            return true;
        }

        if (value is IList list && index >= 0 && index < list.Count)
        {
            current = list[index]!;
            return current != null;
        }

        if (value is IEnumerable enumerable)
        {
            current = enumerable.Cast<object?>().ElementAtOrDefault(index)!;
            return current != null;
        }

        return false;
    }

    private static int ParseItemIndex(string pathPart, int itemIndexStart)
    {
        if (itemIndexStart < 0
            || !pathPart.EndsWith("]", StringComparison.Ordinal)
            || !int.TryParse(pathPart[(itemIndexStart + 1)..^1], out var itemIndex))
            return -1;

        return itemIndex;
    }

    private static IReadOnlyList<IReadOnlyList<TranslationWorkItem>> BuildBatches(
        IReadOnlyCollection<TranslationWorkItem> items,
        AiTranslationSettings settings)
    {
        if (!settings.ConserveApiCalls)
            return items.Select(item => (IReadOnlyList<TranslationWorkItem>)new[] { item }).ToList();

        var batches = new List<IReadOnlyList<TranslationWorkItem>>();
        var current = new List<TranslationWorkItem>();
        var inputLimit = Math.Max(256, settings.MaxInputTokens - Math.Min(settings.MaxOutputTokens, 1024));
        var currentTokens = 0;
        var currentOutputTokens = 0;
        foreach (var item in items)
        {
            var itemTokens = Math.Max(1, (item.SourceText.Length + 3) / 4) + 32;
            var outputTokens = Math.Max(1, (item.SourceText.Length + 3) / 4) * 2 + 64;
            if (current.Count > 0 && (EstimateInputTokens(current.Append(item), item.Language) > inputLimit || currentOutputTokens + outputTokens > settings.MaxOutputTokens))
            {
                batches.Add(current);
                current = new List<TranslationWorkItem>();
                currentTokens = 0;
                currentOutputTokens = 0;
            }
            var boundedSource = item.SourceText;
            var remainingInputTokens = Math.Max(32, inputLimit - currentTokens - 24);
            var remainingOutputTokens = Math.Max(32, settings.MaxOutputTokens - currentOutputTokens - 16);
            var sourceTokenLimit = Math.Min(remainingInputTokens, remainingOutputTokens);
            if ((boundedSource.Length + 3) / 4 > sourceTokenLimit)
                boundedSource = boundedSource[..Math.Min(boundedSource.Length, sourceTokenLimit * 4)];
            var candidate = item with { SourceText = boundedSource };
            while (boundedSource.Length > 0 && EstimateInputTokens(current.Append(candidate), item.Language) > inputLimit)
            {
                boundedSource = boundedSource[..Math.Max(0, boundedSource.Length - 256)];
                candidate = item with { SourceText = boundedSource };
            }
            var boundedItemTokens = Math.Max(1, (boundedSource.Length + 3) / 4) + 32;
            var boundedOutputTokens = Math.Max(1, (boundedSource.Length + 3) / 4) * 2 + 64;
            current.Add(candidate);
            currentTokens += boundedItemTokens;
            currentOutputTokens += boundedOutputTokens;
        }
        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    private static int EstimateInputTokens(IEnumerable<TranslationWorkItem> items, string language)
    {
        var entries = items.Select(item => new { id = item.PropertyName + "|" + item.ContentLink, text = item.SourceText });
        var prompt = TranslationPrompts.BuildChatCompletionPrompt(language, items.FirstOrDefault()?.Tone ?? TranslationTone.Neutral, JsonSerializer.Serialize(entries));
        return (prompt.Length + 3) / 4;
    }

    private static IContent GetWritableClone(IContent content)
    {
        var method = content.GetType().GetMethod("CreateWritableClone", Type.EmptyTypes)
            ?? throw new InvalidOperationException($"Content type '{content.GetType().FullName}' does not support writable cloning.");
        return method.Invoke(content, null) as IContent
            ?? throw new InvalidOperationException($"Content type '{content.GetType().FullName}' returned no writable clone.");
    }

    private sealed record TranslationJob(string JobId, IReadOnlyCollection<TranslationWorkItem> Items);
}
