using EPiServer.Core;

namespace Cheapskate.AITranslator.Models;

public enum LanguageCreationStatus
{
    CreatedDraft,
    SkippedAlreadyExists,
    SkippedDuplicateInRun,
    Failed
}

public class LanguageVersionCreationRequest
{
    public ContentReference ContentLink { get; set; } = null!;
    public IReadOnlyList<string> TargetLanguages { get; set; } = [];
    public bool AllLanguages { get; set; }
    public bool IncludeLinkedContents { get; set; }
    public bool UseAiTranslation { get; set; }
    public TranslationTone Tone { get; set; }
}

public class LanguageVersionItemResult
{
    public ContentReference? ContentLink { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public LanguageCreationStatus Status { get; set; }
    public string? Reason { get; set; }
}

public class LanguageVersionCreationResult
{
    public int TotalProcessed => Items.Count;
    public int TotalCreated => Items.Count(i => i.Status == LanguageCreationStatus.CreatedDraft);
    public int TotalSkipped => Items.Count(i => i.Status == LanguageCreationStatus.SkippedAlreadyExists || i.Status == LanguageCreationStatus.SkippedDuplicateInRun);
    public int TotalFailed => Items.Count(i => i.Status == LanguageCreationStatus.Failed);
    public List<LanguageVersionItemResult> Items { get; set; } = [];
    public List<string> TranslationJobIds { get; set; } = [];
}

public class LanguageOptionDto
{
    public string LanguageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool ExistsOnContent { get; set; }
    public bool IsMasterLanguage { get; set; }
}

public class ContentLanguageOptionsResult
{
    public ContentReference ContentLink { get; set; } = null!;
    public string ContentName { get; set; } = string.Empty;
    public string MasterLanguage { get; set; } = string.Empty;
    public List<LanguageOptionDto> Languages { get; set; } = [];
    public bool AiTranslationConfigured { get; set; }
}
