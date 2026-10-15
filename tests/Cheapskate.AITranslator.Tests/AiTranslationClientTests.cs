using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EPiServer.Core;
using Cheapskate.AITranslator.Models;
using Cheapskate.AITranslator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cheapskate.AITranslator.Tests;

public sealed class AiTranslationClientTests
{
    [Fact]
    public async Task Gemini_UsesBearerKeyAndExtractsJsonTranslations()
    {
        var handler = new RecordingHandler("{\"choices\":[{\"message\":{\"content\":\"[{\\\"id\\\":\\\"Title|42\\\",\\\"translated\\\":\\\"Hallo\\\"}]\"}}]}");
        var client = CreateClient(handler);
        var settings = new AiTranslationSettings
        {
            Provider = AiTranslationProvider.GeminiFlashLite,
            Endpoint = "https://example.test/gemini",
            ApiKey = GetSecret("CHEAPSKATE_GEMINI_API_KEY"),
            Model = "gemini-2.5-flash-lite"
        };

        var result = await client.TranslateAsync([WorkItem()], settings);

        Assert.Equal("Hallo", result["Title|42"]);
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal(settings.ApiKey, handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Gemini_RecoversCompleteEntriesFromTruncatedJsonResponse()
    {
        var handler = new RecordingHandler("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"[{\\\"id\\\":\\\"Title|42\\\",\\\"translated\\\":\\\"Hallo\\\"}, {\\\"id\\\":\\\"Body|42\\\",\\\"translated\\\":\\\"Unvollstaendig\"}}]}");
        var client = CreateClient(handler);
        var settings = new AiTranslationSettings
        {
            Provider = AiTranslationProvider.GeminiFlashLite,
            Endpoint = "https://example.test/gemini",
            ApiKey = GetSecret("CHEAPSKATE_GEMINI_API_KEY"),
            Model = "gemini-2.5-flash-lite"
        };

        var result = await client.TranslateAsync([WorkItem()], settings);

        Assert.Equal("Hallo", result["Title|42"]);
    }

    [Fact]
    public async Task AzureTranslator_SendsSubscriptionKeyAndMapsBatchResponse()
    {
        var handler = new RecordingHandler("[{\"translations\":[{\"text\":\"Bonjour\"}]}]");
        var client = CreateClient(handler);
        var settings = new AiTranslationSettings
        {
            Provider = AiTranslationProvider.AzureTranslator,
            Endpoint = "https://example.test/translate?api-version=3.0",
            ApiKey = GetSecret("CHEAPSKATE_AZURE_TRANSLATOR_API_KEY")
        };

        var result = await client.TranslateAsync([WorkItem()], settings);

        Assert.Equal("Bonjour", result["Title|42"]);
        Assert.Equal(settings.ApiKey, handler.Request!.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.Contains("to=fr", handler.Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Groq_UsesOpenAiCompatibleRequestAndExtractsTranslation()
    {
        var handler = new RecordingHandler("{\"choices\":[{\"message\":{\"content\":\"```json\\n[{\\\"id\\\":\\\"Title|42\\\",\\\"translated\\\":\\\"Hola\\\"}]\\n```\"}}]}");
        var client = CreateClient(handler);
        var settings = new AiTranslationSettings
        {
            Provider = AiTranslationProvider.GroqCloud,
            Endpoint = "https://example.test/groq",
            ApiKey = GetSecret("CHEAPSKATE_GROQ_API_KEY"),
            Model = "llama-3.1-8b-instant"
        };

        var result = await client.TranslateAsync([WorkItem()], settings);

        Assert.Equal("Hola", result["Title|42"]);
        Assert.Equal(settings.ApiKey, handler.Request!.Headers.Authorization!.Parameter);
        using var requestBody = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(settings.Model, requestBody.RootElement.GetProperty("model").GetString());
    }

    private static AiTranslationClient CreateClient(RecordingHandler handler)
    {
        var httpClientFactory = new RecordingHttpClientFactory(handler);
        return new AiTranslationClient([
            new GeminiTranslationProviderClient(httpClientFactory, NullLogger<GeminiTranslationProviderClient>.Instance),
            new AzureTranslationProviderClient(httpClientFactory, NullLogger<AzureTranslationProviderClient>.Instance),
            new DeepLTranslationProviderClient(httpClientFactory, NullLogger<DeepLTranslationProviderClient>.Instance),
            new GroqTranslationProviderClient(httpClientFactory, NullLogger<GroqTranslationProviderClient>.Instance),
            new OtherTranslationProviderClient(httpClientFactory, NullLogger<OtherTranslationProviderClient>.Instance)
        ]);
    }

    private static TranslationWorkItem WorkItem() =>
        new("job", new ContentReference(42), "fr", "Title", "Hello");

    private static string GetSecret(string name) =>
        Environment.GetEnvironmentVariable(name) ?? "unit-test-key-not-a-secret";

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public RecordingHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public RecordingHandler(string response) => _response = response;

        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
