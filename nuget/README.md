# Cheapskate.AITranslator

Optimizely CMS 12 add-on for creating draft language versions and optionally translating language-specific text properties through Google Gemini, GroqCloud, Azure Translator, DeepL, or a custom OpenAI-compatible provider.

## Requirements

- .NET 8.
- Optimizely CMS 12.x.
- A CMS application with Shell UI and edit UI packages enabled.
- At least one configured site language for target language branches.
- An API key for the selected provider when AI translation is enabled.

This package is compatible with CMS versions `>= 12.0.0` and `< 13.0.0`. CMS 13 is not supported by this package.

## Installation

Install the package into the Optimizely CMS web project:

```powershell
dotnet add .\YourCmsProject.csproj package Cheapskate.AITranslator --version 1.0.10
dotnet restore .\YourCmsProject.csproj
dotnet build .\YourCmsProject.csproj
```

Restart the CMS application after installation. The package registers the protected client module and adds **AI Translator** under the CMS Settings menu.

## Configuration

1. Sign in as a CMS administrator.
2. Open **Settings > AI Translator**.
3. Select a provider and enter its API key.
4. Enable **AI translation** and save the settings.

Available providers:

- **Google Gemini**: OpenAI-compatible endpoint; model defaults to `gemini-3.5-flash-lite`.
- **GroqCloud**: OpenAI-compatible endpoint; model defaults to `openai/gpt-oss-20b`.
- **Microsoft Azure Translator**: native Azure Translator API and region.
- **DeepL API Free**: native DeepL API.
- **Other**: custom OpenAI-compatible endpoint, model, and token limits.

The model field is editable for Gemini, GroqCloud, and custom providers. The provider Setup guide displays the relevant model guidance. **Conserve API calls** groups multiple properties into bounded JSON requests using the configured input and output token limits.

Provider credentials are stored in the CMS settings store and are not written to source control.

## User guide

1. Open localizable content in the CMS editor.
2. Select the skate icon command **Create Language Versions**.
3. Choose all missing languages or one specific language.
4. Check **Include linked contents** to create and translate content referenced from ContentArea properties.
5. Check **Use AI translation after creating drafts** when AI translation is configured.
6. Optionally choose a translation tone and create the drafts.

Language creation always creates draft versions and never publishes content. Linked content is processed once per language when the option is enabled. AI translation runs in the background after draft creation; the dialog polls the job status and reports completion.

## License

Copyright (c) 2026 Linh Doan

This project is open source under the [MIT License](LICENSE).
