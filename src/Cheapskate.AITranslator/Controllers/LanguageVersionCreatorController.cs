using EPiServer.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Cheapskate.AITranslator.Models;
using Cheapskate.AITranslator.Services;

namespace Cheapskate.AITranslator.Controllers;

[ApiController]
[Route("api/cheapskate/ai-translator/language-version-creator")]
[Authorize]
public class LanguageVersionCreatorController : ControllerBase
{
    private readonly ILanguageVersionCreationService _creationService;
    private readonly ITranslationJobQueue _translationJobs;

    public LanguageVersionCreatorController(ILanguageVersionCreationService creationService, ITranslationJobQueue translationJobs)
    {
        _creationService = creationService ?? throw new ArgumentNullException(nameof(creationService));
        _translationJobs = translationJobs ?? throw new ArgumentNullException(nameof(translationJobs));
    }

    [HttpGet("is-localizable")]
    public ActionResult IsLocalizable([FromQuery] string contentLink)
    {
        if (string.IsNullOrWhiteSpace(contentLink))
        {
            return BadRequest("ContentLink parameter is required.");
        }

        if (!ContentReference.TryParse(contentLink, out var parsedLink))
        {
            return BadRequest($"Invalid ContentReference format: '{contentLink}'.");
        }

        try
        {
            if (!_creationService.IsContentLocalizable(parsedLink))
            {
                return BadRequest(new { message = "Language versions can only be created for localizable content." });
            }

            return Ok();
        }
        catch (EPiServer.Core.ContentNotFoundException)
        {
            return NotFound($"Content with reference '{contentLink}' was not found.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpGet("options")]
    public ActionResult<ContentLanguageOptionsResult> GetOptions([FromQuery] string contentLink)
    {
        if (string.IsNullOrWhiteSpace(contentLink))
        {
            return BadRequest("ContentLink parameter is required.");
        }

        if (!ContentReference.TryParse(contentLink, out var parsedLink))
        {
            return BadRequest($"Invalid ContentReference format: '{contentLink}'.");
        }

        try
        {
            var options = _creationService.GetLanguageOptions(parsedLink);
            return Ok(options);
        }
        catch (EPiServer.Core.ContentNotFoundException)
        {
            return NotFound($"Content with reference '{contentLink}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpPost("create")]
    public async Task<ActionResult<LanguageVersionCreationResult>> Create(
        [FromBody] LanguageVersionCreationApiRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ContentLink))
        {
            return BadRequest("Request and ContentLink are required.");
        }

        if (!ContentReference.TryParse(request.ContentLink, out var parsedLink))
        {
            return BadRequest($"Invalid ContentReference format: '{request.ContentLink}'.");
        }

        var serviceRequest = new LanguageVersionCreationRequest
        {
            ContentLink = parsedLink,
            TargetLanguages = request.TargetLanguages ?? [],
            AllLanguages = request.AllLanguages,
            IncludeLinkedContents = request.IncludeLinkedContents,
            UseAiTranslation = request.UseAiTranslation,
            Tone = request.Tone
        };

        try
        {
            var result = await _creationService.CreateLanguageVersionsAsync(serviceRequest, cancellationToken);
            return Ok(result);
        }
        catch (EPiServer.Core.ContentNotFoundException)
        {
            return NotFound($"Content with reference '{request.ContentLink}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = ex.Message });
        }
    }

    [HttpGet("translation-status/{jobId}")]
    public ActionResult<TranslationJobStatus> GetTranslationStatus(string jobId)
    {
        var status = _translationJobs.GetStatus(jobId);
        return status == null ? NotFound() : Ok(status);
    }
}

public class LanguageVersionCreationApiRequest
{
    public string ContentLink { get; set; } = string.Empty;
    public List<string>? TargetLanguages { get; set; }
    public bool AllLanguages { get; set; }
    public bool IncludeLinkedContents { get; set; }
    public bool UseAiTranslation { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TranslationTone Tone { get; set; }
}
