using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.API.Services.AI.Models;
using PointOfSale.API.Services.AI.Router;

namespace PointOfSale.API.Services.AI;

/// <summary>
/// Routes messages to the AI provider and processes responses.
/// Model-agnostic - works with any LLM that supports OpenAI-compatible chat completions.
/// 
/// PHASE 2 EXTENSION:
/// - Supports multi-model routing via IModelRouter when Models are configured
/// - Falls back to legacy single-model behavior for backward compatibility
/// 
/// ARCHITECTURAL BOUNDARY:
/// - This service is the ONLY point of contact with the AI
/// - All responses are validated before returning
/// - Fallback behavior if AI fails
/// - No exposed model names or internal details
/// </summary>
public class AIRouterService : IAIRouterService
{
    private readonly HttpClient _httpClient;
    private readonly IAIPromptManager _promptManager;
    private readonly IAIResponseValidator _responseValidator;
    private readonly IAIRateLimiter _rateLimiter;
    private readonly ISafeResponseBuilder _safeResponseBuilder;
    private readonly IModelRouter? _modelRouter;
    private readonly CommerceAIOptions _options;
    private readonly ILogger<AIRouterService> _logger;

    public AIRouterService(
        HttpClient httpClient,
        IAIPromptManager promptManager,
        IAIResponseValidator responseValidator,
        IAIRateLimiter rateLimiter,
        ISafeResponseBuilder safeResponseBuilder,
        IOptions<CommerceAIOptions> options,
        ILogger<AIRouterService> logger,
        IModelRouter? modelRouter = null)
    {
        _httpClient = httpClient;
        _promptManager = promptManager;
        _responseValidator = responseValidator;
        _rateLimiter = rateLimiter;
        _safeResponseBuilder = safeResponseBuilder;
        _modelRouter = modelRouter;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> ProcessAsync(
        CommerceContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("AI assistant is disabled, returning fallback response");
            return CreateFallbackResponse("The assistant is currently unavailable. Please try again later.");
        }

        // PHASE 2: Use ModelRouter if configured with multiple models
        if (_modelRouter != null && _options.Models.Count > 0)
        {
            _logger.LogDebug("Using multi-model routing (Phase 2)");
            return await _modelRouter.RouteAsync(context, cancellationToken);
        }

        // Legacy single-model behavior (Phase 1)
        if (!AIProviderTransport.ValidConfiguration(_options.Endpoint, _options.Model, _options.ApiKey, _options.TimeoutSeconds, _options.MaxTokens, _options.Temperature))
            return CreateFallbackResponse("The assistant needs server configuration before it can answer.");
        return await ProcessLegacyAsync(context, cancellationToken);
    }

    /// <summary>
    /// Legacy single-model processing (Phase 1 behavior).
    /// </summary>
    private async Task<ChatResponse> ProcessLegacyAsync(
        CommerceContext context,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();

        try
        {
            var rateLimitResult = await _rateLimiter.CheckAsync(context.TenantId, cancellationToken);
            if (!rateLimitResult.Allowed)
            {
                _logger.LogWarning(
                    "Rate limit exceeded on legacy AI route | TenantId: {TenantId} | CorrelationId: {CorrelationId}",
                    context.TenantId, correlationId);
                return _safeResponseBuilder.CreateRateLimitedResponse();
            }

            // Build prompts
            var systemPrompt = _promptManager.GetSystemPrompt();
            var userPrompt = _promptManager.BuildUserPrompt(context);
            if (!AIProviderTransport.WithinPromptBudget(systemPrompt, userPrompt))
                return CreateFallbackResponse("This request contains too much context. Please narrow your question.");

            if (_options.EnableDebugLogging)
            {
                _logger.LogDebug(
                    "AI Request | CorrelationId: {CorrelationId} | SystemPrompt: {SystemPromptLength} chars | UserPrompt: {UserPromptLength} chars",
                    correlationId, systemPrompt.Length, userPrompt.Length);
            }

            var completion = await AIProviderTransport.CompleteAsync(_httpClient, _options, systemPrompt, userPrompt, cancellationToken,
                () => _rateLimiter.CheckAsync(context.TenantId, cancellationToken));
            var aiResponse = completion.Content;
            var tokensUsed = completion.TokensUsed;
            await _rateLimiter.RecordRequestAsync(
                context.TenantId,
                tokensUsed,
                _options.Model,
                cancellationToken);

            if (string.IsNullOrEmpty(aiResponse))
            {
                _logger.LogWarning(
                    "Empty AI response | CorrelationId: {CorrelationId}",
                    correlationId);
                return CreateFallbackResponse("I couldn't generate a response. Please try rephrasing your question.");
            }

            if (_options.EnableDebugLogging)
            {
                _logger.LogDebug(
                    "AI Response | CorrelationId: {CorrelationId} | Length: {Length}", correlationId, aiResponse.Length);
            }

            // Validate and parse response
            if (_responseValidator.TryValidate(aiResponse, context, out var parsed, out var error))
            {
                _logger.LogInformation(
                    "AI request successful | CorrelationId: {CorrelationId} | Type: {Type} | HasDraft: {HasDraft}",
                    correlationId, parsed!.Type, parsed.OrderDraft != null);
                return parsed;
            }
            else
            {
                _logger.LogWarning(
                    "AI response validation failed | CorrelationId: {CorrelationId} | Error: {Error}",
                    correlationId, error);
                return CreateFallbackResponse("I need a bit more information to help you correctly.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AIProviderException ex)
        {
            _logger.LogWarning("AI provider failure | Code: {Code} | ProviderStatus: {ProviderStatus} | CorrelationId: {CorrelationId}", ex.Code, ex.ProviderStatus, correlationId);
            var response = CreateFallbackResponse(ex.Message);
            response.ErrorCode = ex.Code;
            response.RetryAfterSeconds = ex.RetryAfterSeconds;
            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AI request timed out | CorrelationId: {CorrelationId}", correlationId);
            return CreateFallbackResponse("The request is taking too long. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "AI request failed | CorrelationId: {CorrelationId}", correlationId);
            return CreateFallbackResponse("I'm having trouble connecting right now. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in AI processing | CorrelationId: {CorrelationId}", correlationId);
            return CreateFallbackResponse("Something went wrong. Please try again.");
        }
    }

    /// <summary>
    /// Create a fallback response when AI fails.
    /// PHASE 2: Uses Phase 2 safe response format.
    /// </summary>
    private ChatResponse CreateFallbackResponse(string message)
    {
        return new ChatResponse
        {
            Type = ChatResponseTypes.Message,
            Message = message,
            OrderDraft = null,
            MissingInfo = new List<string>(),
            IsFallback = true
        };
    }
}
