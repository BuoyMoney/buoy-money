using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services.Ai;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Which AI provider this server runs and what each feature can do with it. The app reads the
/// status to hide AI screens when AI is off or a feature isn't available; the admin gets the
/// connection details and can run a live check. The provider itself is a server setting
/// (<c>Ai:*</c>, applied at startup), not something this controller changes.
/// </summary>
[Authorize]
public class AiSettingsController : BaseController
{
  private readonly IAiProvider _provider;
  private readonly AiProviderOptions _options;
  private readonly EditionInfo _edition;
  private readonly AIModelConfiguration _models;

  public AiSettingsController(IAiProvider provider, AiProviderOptions options, EditionInfo edition, AIModelConfiguration models)
  {
    _provider = provider;
    _options = options;
    _edition = edition;
    _models = models;
  }

  [HttpGet]
  public IActionResult GetStatus()
  {
    var features = AiFeatureCatalog.Describe(_provider, _options);
    object? details = null;
    if (User.IsInRole("Admin"))
    {
      details = new
      {
        baseUrl = _options.IsOpenAiCompatible ? _options.BaseUrl : null,
        model = _options.IsOpenAiCompatible ? _options.Model : null,
        featureModels = _options.FeatureModels,
        embeddingModel = _options.EmbeddingModel,
        embeddingDimensions = _options.EmbeddingDimensions,
        // Native Gemini reads its key from GOOGLE_API_KEY / Gemini:ApiKey, not Ai:ApiKey.
        apiKeySet = _options.IsGemini ? _provider.IsConfigured : !string.IsNullOrWhiteSpace(_options.ApiKey),
        setupProblem = _provider.IsConfigured ? null : AiFeatureCatalog.SetupProblemFor(_options),
      };
    }

    return Ok(new
    {
      enabled = _provider.IsConfigured,
      provider = _provider.Id,
      edition = _edition.Name,
      features,
      details,
    });
  }

  /// <summary>Two small model calls (no user data) to show whether the model can be reached and answers in the categorization format.</summary>
  [HttpPost("probe")]
  [Authorize(Roles = "Admin")]
  public async Task<IActionResult> Probe(CancellationToken cancellationToken)
  {
    var model = _models.GetPrimaryModel(SubscriptionTier.Standard);
    var steps = await AiProbe.RunAsync(_provider, model, cancellationToken);
    return Ok(new { ok = steps.Count > 0 && steps.All(s => s.Ok), steps });
  }
}
