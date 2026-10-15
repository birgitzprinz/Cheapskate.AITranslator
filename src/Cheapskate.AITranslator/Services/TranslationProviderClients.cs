using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cheapskate.AITranslator.Models;
using Microsoft.Extensions.Logging;

namespace Cheapskate.AITranslator.Services;

public interface IAiTranslationProviderClient
{
    AiTranslationProvider Provider { get; }

    Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationWorkItem> batch,
        AiTranslationSettings settings,
        CancellationToken cancellationToken = default);
}

public abstract class ChatCompletionTranslationProviderClient : IAiTranslationProviderClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    protected ChatCompletionTranslationProviderClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public abstract AiTranslationProvider Provider { get; }

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationWorkItem> batch,
        AiTranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var entries = batch.Select(item => new { id = item.PropertyName + "|" + item.ContentLink, text = item.SourceText }).ToArray();
        var prompt = TranslationPrompts.BuildChatCompletionPrompt(batch[0].Language, batch[0].Tone, JsonSerializer.Serialize(entries));
        var predictedOutputTokens = settings.MaxOutputTokens;
        _logger.LogInformation(
            "AI translation request: provider {Provider}, model {Model}, batch size {BatchSize}, configured MaxOutputTokens {ConfiguredMaxOutputTokens}, computed max_tokens {ComputedMaxTokens}",
            Provider,
            settings.Model,
            batch.Count,
            settings.MaxOutputTokens,
            predictedOutputTokens);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = settings.Model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.1,
            max_tokens = predictedOutputTokens
        }), Encoding.UTF8, "application/json");

        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseContent = await AiTranslationHttpResponse.ReadAndValidateAsync(response, Provider, settings.Endpoint, _logger, cancellationToken);
        using var document = JsonDocument.Parse(responseContent);
        var choice = document.RootElement.GetProperty("choices")[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString()
            : null;
        var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "[]";
        content = content.Trim().Trim('`');
        if (content.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            content = content[4..].Trim();

        _logger.LogWarning(
            "AI translation provider {Provider} response payload: {ResponsePayload}",
            Provider,
            responseContent);
        _logger.LogWarning(
            "AI translation provider {Provider} message content: {MessageContent}",
            Provider,
            content);

        if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "AI translation provider {Provider} truncated its JSON response at the output token limit. Attempting to recover complete translation objects from the partial response.",
                Provider);
        }

        var parsedTranslations = new List<(string Id, string Text)>();
        foreach (var entry in ParseTranslationEntries(content))
        {
            if (!entry.TryGetProperty("id", out var id))
                continue;

            var translationProperty = entry.TryGetProperty("translated", out var translated)
                ? translated
                : entry.TryGetProperty("text", out var text) ? text : default;
            if (translationProperty.ValueKind == JsonValueKind.String)
            {
                parsedTranslations.Add((id.GetString() ?? string.Empty, translationProperty.GetString() ?? string.Empty));
            }
        }

        var expectedOrder = batch
            .Select(item => item.PropertyName + "|" + item.ContentLink)
            .ToList();
        var expectedIds = expectedOrder.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmatchedTranslations = new Queue<(string Id, string Text)>(parsedTranslations);
        foreach (var translation in parsedTranslations)
        {
            if (expectedIds.Contains(translation.Id))
            {
                result[translation.Id] = translation.Text;
                unmatchedTranslations = new Queue<(string Id, string Text)>(
                    unmatchedTranslations.Where(item => !string.Equals(item.Id, translation.Id, StringComparison.Ordinal)));
            }
        }

        // Some compatible endpoints rewrite or omit the ids despite the prompt.
        // Preserve batch order as a fallback so those responses can still be applied safely.
        foreach (var expectedId in expectedOrder.Where(id => !result.ContainsKey(id)))
        {
            while (unmatchedTranslations.Count > 0 && expectedIds.Contains(unmatchedTranslations.Peek().Id))
                unmatchedTranslations.Dequeue();

            if (unmatchedTranslations.Count == 0)
                break;

            var fallback = unmatchedTranslations.Dequeue();
            result[expectedId] = fallback.Text;
            _logger.LogWarning(
                "AI translation provider {Provider} returned unexpected translation id {ReturnedId}; mapped it by batch order to {ExpectedId}.",
                Provider,
                fallback.Id,
                expectedId);
        }

        _logger.LogInformation(
            "AI translation provider {Provider} returned {ReturnedCount} translation entries; matched {MatchedCount} of {ExpectedCount} requested entries. Returned ids: {ReturnedIds}",
            Provider,
            parsedTranslations.Count,
            result.Count,
            expectedIds.Count,
            string.Join(", ", parsedTranslations.Select(item => item.Id)));
        return result;
    }

    private static IEnumerable<JsonElement> ParseTranslationEntries(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement[]>(content) ?? [];
        }
        catch (JsonException)
        {
            var entries = new List<JsonElement>();
            var objectStart = -1;
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                if (inString)
                {
                    if (escaped)
                        escaped = false;
                    else if (character == '\\')
                        escaped = true;
                    else if (character == '"')
                        inString = false;
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    continue;
                }

                if (character == '{')
                {
                    if (depth == 0)
                        objectStart = index;
                    depth++;
                }
                else if (character == '}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        try
                        {
                            using var objectDocument = JsonDocument.Parse(content[objectStart..(index + 1)]);
                            entries.Add(objectDocument.RootElement.Clone());
                        }
                        catch (JsonException)
                        {
                            // Ignore an incomplete or malformed object and keep any earlier results.
                        }

                        objectStart = -1;
                    }
                }
            }

            return entries;
        }
    }
}

public sealed class GeminiTranslationProviderClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GeminiTranslationProviderClient> logger)
    : ChatCompletionTranslationProviderClient(httpClientFactory, logger)
{
    public override AiTranslationProvider Provider => AiTranslationProvider.GeminiFlashLite;
}

public sealed class GroqTranslationProviderClient(
    IHttpClientFactory httpClientFactory,
    ILogger<GroqTranslationProviderClient> logger)
    : ChatCompletionTranslationProviderClient(httpClientFactory, logger)
{
    public override AiTranslationProvider Provider => AiTranslationProvider.GroqCloud;
}

public sealed class OtherTranslationProviderClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OtherTranslationProviderClient> logger)
    : ChatCompletionTranslationProviderClient(httpClientFactory, logger)
{
    public override AiTranslationProvider Provider => AiTranslationProvider.Other;
}

public sealed class AzureTranslationProviderClient : IAiTranslationProviderClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureTranslationProviderClient> _logger;

    public AzureTranslationProviderClient(
        IHttpClientFactory httpClientFactory,
        ILogger<AzureTranslationProviderClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public AiTranslationProvider Provider => AiTranslationProvider.AzureTranslator;

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationWorkItem> batch,
        AiTranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        var endpoint = settings.Endpoint + (settings.Endpoint.Contains('?') ? "&" : "?") + "to=" + Uri.EscapeDataString(batch[0].Language);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var apiKey = settings.ApiKey?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(settings.AzureRegion))
            request.Headers.Add("Ocp-Apim-Subscription-Region", settings.AzureRegion.Trim());
        else
            _logger.LogWarning("Azure Translator region is not configured. Regional or multi-service Translator resources may reject this request.");
        _logger.LogInformation(
            "Azure Translator request: target language {Language}, API key configured {ApiKeyConfigured}, region configured {RegionConfigured}",
            batch[0].Language,
            !string.IsNullOrWhiteSpace(apiKey),
            !string.IsNullOrWhiteSpace(settings.AzureRegion));
        request.Content = new StringContent(JsonSerializer.Serialize(batch.Select(item => new { Text = item.SourceText })), Encoding.UTF8, "application/json");
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseContent = await AiTranslationHttpResponse.ReadAndValidateAsync(response, Provider, endpoint, _logger, cancellationToken);
        using var document = JsonDocument.Parse(responseContent);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Count && index < document.RootElement.GetArrayLength(); index++)
        {
            var translated = document.RootElement[index].GetProperty("translations")[0].GetProperty("text").GetString();
            result[batch[index].PropertyName + "|" + batch[index].ContentLink] = translated ?? string.Empty;
        }
        return result;
    }
}

public sealed class DeepLTranslationProviderClient : IAiTranslationProviderClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepLTranslationProviderClient> _logger;

    public DeepLTranslationProviderClient(
        IHttpClientFactory httpClientFactory,
        ILogger<DeepLTranslationProviderClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public AiTranslationProvider Provider => AiTranslationProvider.DeepLFree;

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<TranslationWorkItem> batch,
        AiTranslationSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", settings.ApiKey);
        var form = new List<KeyValuePair<string, string>>
        {
            new("target_lang", batch[0].Language.Split('-')[0].ToUpperInvariant())
        };
        form.AddRange(batch.Select(item => new KeyValuePair<string, string>("text", item.SourceText)));
        request.Content = new FormUrlEncodedContent(form);
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var responseContent = await AiTranslationHttpResponse.ReadAndValidateAsync(response, Provider, settings.Endpoint, _logger, cancellationToken);
        using var document = JsonDocument.Parse(responseContent);
        var translations = document.RootElement.GetProperty("translations");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Count && index < translations.GetArrayLength(); index++)
            result[batch[index].PropertyName + "|" + batch[index].ContentLink] = translations[index].GetProperty("text").GetString() ?? string.Empty;
        return result;
    }
}

internal static class AiTranslationHttpResponse
{
    public static async Task<string> ReadAndValidateAsync(
        HttpResponseMessage response,
        AiTranslationProvider provider,
        string endpoint,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "AI translation request failed. Provider: {Provider}; StatusCode: {StatusCode}; Reason: {ReasonPhrase}; Endpoint: {Endpoint}; Response: {ResponseContent}",
                provider,
                (int)response.StatusCode,
                response.ReasonPhrase,
                endpoint,
                content);
        }

        response.EnsureSuccessStatusCode();
        return content;
    }
}
