using Microsoft.Extensions.Logging;
using BuoyAI.Core.Enums;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Partial class containing Gemini API methods:
/// - CallGeminiWithToolsAsync (function calling)
/// Plain text generation goes through IGeminiApiClient.
/// </summary>
public partial class AIService
{
    private class GeminiFunctionCallResponse
    {
        public string? Text { get; set; }
        public FunctionCall? FunctionCall { get; set; }
        public List<Part>? ModelParts { get; set; }

        /// <summary>What Gemini reported for this call: prompt tokens, and output tokens including thinking. Null when it sent none.</summary>
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public bool HasFunctionCall => FunctionCall != null;
    }

    // AI hardening (#637): explicit Gemini safety thresholds as a belt-and-suspenders layer
    // under the system-prompt guardrails (#635). Block medium-probability-and-above across the
    // four core harm categories. The assistant is scoped to budgeting/personal finance, so this
    // should not over-block normal finance content.
    private static List<SafetySetting> BuildFinancialSafetySettings() => new()
    {
        new SafetySetting { Category = HarmCategory.HarmCategoryHarassment,       Threshold = HarmBlockThreshold.BlockMediumAndAbove },
        new SafetySetting { Category = HarmCategory.HarmCategoryHateSpeech,       Threshold = HarmBlockThreshold.BlockMediumAndAbove },
        new SafetySetting { Category = HarmCategory.HarmCategorySexuallyExplicit, Threshold = HarmBlockThreshold.BlockMediumAndAbove },
        new SafetySetting { Category = HarmCategory.HarmCategoryDangerousContent, Threshold = HarmBlockThreshold.BlockMediumAndAbove },
    };

    // Shown to the user when Gemini blocks a turn for safety (a SAFETY finish reason or a blocked
    // prompt). Keeps the assistant on-brand instead of surfacing an empty/error response.
    private const string SafetyRefusalMessage =
        "I can't help with that — I'm here for budgeting and personal finance. " +
        "Ask me about your spending, budgets, savings goals, or transactions.";

    /// <inheritdoc />
    public Task<string> GenerateTextAsync(string prompt)
        => _geminiApiClient.GenerateContentAsync(prompt, _modelConfiguration.GetPrimaryModel(BuoyAI.Core.Enums.SubscriptionTier.Admin));

    private async Task<GeminiFunctionCallResponse> CallGeminiWithToolsAsync(
        List<Content> contents,
        string model,
        List<FunctionDeclaration> functionDeclarations,
        string? systemInstruction = null,
        SubscriptionTier? userTier = null,
        string? cachedContentName = null)
    {
        var hasGoogleSearch = false; // Disabled when using function calling
        _logger.LogInformation("Calling Gemini API with native function calling. Model: {Model}, Tools: {ToolCount}, GoogleSearch: {HasGoogleSearch}", 
            model, functionDeclarations.Count, hasGoogleSearch);
        
        var toolNames = string.Join(", ", functionDeclarations.Select(f => f.Name));
        _logger.LogInformation("Available tools for Gemini: {ToolNames}", toolNames);
        
        var apiKey = _geminiApiKey;
        
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("No Gemini API key available for native function calling");
            return new GeminiFunctionCallResponse { Text = "API key not configured. Please try again later." };
        }

        try
        {
            // Apply HTTP/2 workarounds
            AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2FlowControl.DisableDynamicWindowSizing", true);
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER", "0");
            AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);
            
            var googleAI = new GoogleAI(apiKey);
            var generativeModel = googleAI.GenerativeModel(model);
            
            var tools = new Tools();
            tools.Add(new Tool { FunctionDeclarations = functionDeclarations });
            
            // Low thinking for function calling: models think by default, which could leave
            // thought-only turns with no answer or call. Gemini deprecated thinking_budget (newer
            // models reject it with 400) and custom sampling (temperature, top_p, top_k are ignored
            // since 3.6 Flash and will be rejected), so no sampling settings. GeminiThinking.Low sets
            // thinking level "low" on Gemini 3+ and keeps budget 0 on the Gemini 2.5 fallback,
            // which has no thinking levels.
            var generationConfig = new GenerationConfig
            {
                ThinkingConfig = GeminiThinking.Low(model)
            };
            
            var toolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingConfigMode.Auto
                }
            };
            
            _logger.LogDebug("Generating content with {ContentCount} content parts and {ToolCount} tools", 
                contents.Count, functionDeclarations.Count);
            
            // #743: when a prompt cache is referenced, Gemini forbids also sending tools/tool_config/
            // system_instruction in the request — they live inside the CachedContent. Omit them here.
            var usingCache = !string.IsNullOrEmpty(cachedContentName);
            var request = new GenerateContentRequest
            {
                Contents = contents,
                Tools = usingCache ? null : tools,
                ToolConfig = usingCache ? null : toolConfig,
                GenerationConfig = generationConfig,
                SafetySettings = BuildFinancialSafetySettings(),  // #637
                SystemInstruction = usingCache || string.IsNullOrEmpty(systemInstruction)
                    ? null
                    : new Content(systemInstruction),
                CachedContent = usingCache ? cachedContentName : null,
            };
            
            try
            {
                var requestJson = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                _logger.LogDebug("Gemini request JSON (Debug only, first 4000 chars): {RequestJson}", 
                    requestJson.Length > 4000 ? requestJson[..4000] : requestJson);
            }
            catch (Exception serEx)
            {
                _logger.LogWarning("Failed to serialize request for logging: {Error}", serEx.Message);
            }
            
            var response = await generativeModel.GenerateContent(request);

            _logger.LogInformation("Gemini response received. HasCandidates={HasCandidates}",
                response?.Candidates?.Any() == true);

            // Real token accounting from Gemini (the pipeline otherwise estimates via length/4).
            // cached>0 means prompt/context caching (implicit or explicit) discounted this call —
            // the signal for the #740 caching lever.
            var usage = response?.UsageMetadata;
            int? realInput = usage?.PromptTokenCount;
            // Thinking tokens are billed as output but are not in CandidatesTokenCount.
            int? realOutput = usage == null ? null : (usage.CandidatesTokenCount ?? 0) + (usage.ThoughtsTokenCount ?? 0);
            if (usage != null)
            {
                _logger.LogInformation(
                    "Gemini usage [{Model}]: prompt={Prompt} cached={Cached} output={Output} total={Total} (tools={Tools}, explicitCache={ExplicitCache})",
                    model, usage.PromptTokenCount, usage.CachedContentTokenCount,
                    usage.CandidatesTokenCount, usage.TotalTokenCount, functionDeclarations.Count,
                    usingCache ? "yes" : "no");
            }
            
            var parts = response?.Candidates?.FirstOrDefault()?.Content?.Parts;
            string? accumulatedText = null;
            string? thinkingText = null;
            bool hasThinkingParts = false;
            
            if (parts != null)
            {
                foreach (var part in parts)
                {
                    _logger.LogInformation("Response part: FunctionCall={HasFunctionCall}, Text={HasText}, Thought={IsThought}, ThoughtSignature={HasThoughtSig}", 
                        part.FunctionCall != null, 
                        part.Text != null,
                        part.Thought,
                        part.ThoughtSignature != null);
                    
                    if (part.FunctionCall != null)
                    {
                        _logger.LogInformation("Found FunctionCall in part: {FunctionName}, HasThoughtSignature={HasSig}", 
                            part.FunctionCall.Name, part.ThoughtSignature != null);
                        var modelParts = response?.Candidates?.FirstOrDefault()?.Content?.Parts;
                        return new GeminiFunctionCallResponse 
                        { 
                            FunctionCall = part.FunctionCall,
                            ModelParts = modelParts,
                            InputTokens = realInput,
                            OutputTokens = realOutput
                        };
                    }
                    
                    // Track thinking vs answer parts
                    if (part.Thought == true)
                    {
                        hasThinkingParts = true;
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            thinkingText = string.IsNullOrEmpty(thinkingText) 
                                ? part.Text 
                                : thinkingText + part.Text;
                        }
                    }
                    else if (!string.IsNullOrEmpty(part.Text))
                    {
                        // Non-thought text is the actual answer
                        accumulatedText = string.IsNullOrEmpty(accumulatedText) 
                            ? part.Text 
                            : accumulatedText + part.Text;
                    }
                }
            }
            
            // Check for function call using convenience property as fallback
            var functionCalls = response?.FunctionCalls;
            if (functionCalls != null && functionCalls.Count > 0)
            {
                var functionCall = functionCalls[0];
                var modelParts = response?.Candidates?.FirstOrDefault()?.Content?.Parts;
                _logger.LogInformation("Gemini returned function call (via FunctionCalls): {FunctionName}", functionCall?.Name);
                return new GeminiFunctionCallResponse 
                { 
                    FunctionCall = functionCall,
                    ModelParts = modelParts,
                    InputTokens = realInput,
                    OutputTokens = realOutput
                };
            }
            
            // Otherwise return text (either accumulated from parts or from convenience property)
            var textResponse = accumulatedText ?? response?.Text;
            if (!string.IsNullOrEmpty(textResponse))
            {
                _logger.LogInformation("Gemini returned text response. Length: {Length}, FromAccumulated={FromAccumulated}", 
                    textResponse.Length, accumulatedText != null);
                return new GeminiFunctionCallResponse { Text = textResponse, InputTokens = realInput, OutputTokens = realOutput };
            }
            
            // Log what we actually got when there's no text or function call
            _logger.LogWarning("Gemini response had no usable text and no function call. " +
                "HasCandidates={HasCandidates}, CandidateCount={CandidateCount}, " +
                "FirstCandidateFinishReason={FinishReason}, FirstCandidateHasContent={HasContent}, " +
                "HadThinkingParts={HadThinking}, ThinkingTextLength={ThinkingLen}",
                response?.Candidates != null,
                response?.Candidates?.Count ?? 0,
                response?.Candidates?.FirstOrDefault()?.FinishReason,
                response?.Candidates?.FirstOrDefault()?.Content != null,
                hasThinkingParts,
                thinkingText?.Length ?? 0);
            
            // #637: a safety-blocked turn → clean, on-brand refusal instead of an empty/retry
            // response. Covers both a blocked prompt (PromptFeedback) and a SAFETY finish reason.
            var candidateFinishReason = response?.Candidates?.FirstOrDefault()?.FinishReason;
            if (response?.PromptFeedback?.BlockReason is { } blockReason
                && blockReason != BlockReason.BlockReasonUnspecified)
            {
                _logger.LogWarning("Gemini blocked the prompt for safety: {BlockReason}", blockReason);
                return new GeminiFunctionCallResponse { Text = SafetyRefusalMessage, InputTokens = realInput, OutputTokens = realOutput };
            }
            if (candidateFinishReason == FinishReason.Safety)
            {
                _logger.LogWarning("Gemini stopped generation for safety (FinishReason=Safety).");
                return new GeminiFunctionCallResponse { Text = SafetyRefusalMessage, InputTokens = realInput, OutputTokens = realOutput };
            }
            if (candidateFinishReason != null)
            {
                _logger.LogWarning("Gemini finished with reason: {FinishReason}", candidateFinishReason);
            }
            
            // If we only got thinking parts but no answer, return null to signal "not done yet"
            // The caller can decide whether to retry or handle this case
            if (hasThinkingParts && string.IsNullOrEmpty(accumulatedText))
            {
                _logger.LogInformation("Response contained only thinking parts (Thought=true) with no answer. " +
                    "This may indicate the model is still reasoning. ThinkingPreview: {Preview}",
                    thinkingText?.Length > 100 ? thinkingText[..100] + "..." : thinkingText);
                // Return empty response - caller will handle retry logic
                return new GeminiFunctionCallResponse { Text = null, InputTokens = realInput, OutputTokens = realOutput };
            }
            
            // Also return null when response has content but no usable text or function call
            // This allows the caller to add a nudge prompt to get the model to respond
            _logger.LogInformation("Gemini returned empty response (no text, no function call, no thinking). Returning null for retry logic.");
            return new GeminiFunctionCallResponse { Text = null, InputTokens = realInput, OutputTokens = realOutput };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error in native function calling: {Message}", ex.Message);
            return new GeminiFunctionCallResponse { Text = "Sorry, I encountered a connection error. Please try again." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in native function calling: {Message}. Type: {ExType}", ex.Message, ex.GetType().Name);
            return new GeminiFunctionCallResponse { Text = "Sorry, I encountered an error processing your request." };
        }
    }
}
