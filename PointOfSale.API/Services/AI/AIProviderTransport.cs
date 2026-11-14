using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using PointOfSale.API.Services.AI.Models;

namespace PointOfSale.API.Services.AI;

internal static class AIProviderTransport
{
    internal static bool IsFreeModel(string model) => model == "openrouter/free" || model.EndsWith(":free", StringComparison.OrdinalIgnoreCase);

    // These transports share the chat-completions JSON contract. Azure uses its
    // deployment endpoint (including api-version) and api-key authentication.
    internal static AIProviderException? ConfigurationError(CommerceAIOptions options)
    {
        var provider = options.Provider?.Trim().ToLowerInvariant();
        if (provider is not ("openrouter" or "openai" or "azure" or "local"))
            return new(503, "AI provider unsupported", "CommerceAI:Provider must be openrouter, openai, azure or local.");
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ApiKey)) missing.Add("CommerceAI:ApiKey");
        if (string.IsNullOrWhiteSpace(options.Endpoint)) missing.Add("CommerceAI:Endpoint");
        if (string.IsNullOrWhiteSpace(options.Model)) missing.Add("CommerceAI:Model");
        if (missing.Count > 0)
            return new(503, "AI configuration missing", $"Required server settings are missing: {string.Join(", ", missing)}.");
        if (!ValidConfiguration(options.Endpoint, options.Model, options.ApiKey, options.TimeoutSeconds, options.MaxTokens, options.Temperature))
            return new(503, "AI configuration invalid", "Check CommerceAI:Endpoint (absolute HTTP(S) URL without credentials), TimeoutSeconds (1–120), MaxTokens (1–32768) and Temperature (0–1).");
        if (options.FreeModelsOnly && (provider == "openrouter" || new Uri(options.Endpoint).Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)) && !IsFreeModel(options.Model))
            return new(503, "AI model policy conflict", "CommerceAI:FreeModelsOnly is enabled, but CommerceAI:Model is not openrouter/free or a :free model. Select a free model, or explicitly authorize paid inference with CommerceAI:FreeModelsOnly=false.");
        return null;
    }

    internal static async Task<AICompletion> CompleteAsync(HttpClient client, CommerceAIOptions options,
        string system, string user, CancellationToken cancellationToken, Func<Task<Router.RateLimitResult>>? admitRetry = null)
    {
        if (ConfigurationError(options) is { } configurationError) throw configurationError;
        var provider = options.Provider.Trim().ToLowerInvariant();
        if (!WithinPromptBudget(system, user))
            throw new AIProviderException(400, "AI context too large", "Please shorten your request.");

        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model,
            ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["response_format"] = new { type = "json_object" }
        };
        // Filter to endpoints that support JSON output. Do not disable reasoning:
        // some free endpoints require it and reject reasoning.enabled=false.
        if (provider == "openrouter")
            body["provider"] = new { require_parameters = true };
        var json = JsonSerializer.Serialize(body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (provider == "azure") request.Headers.Add("api-key", options.ApiKey);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            if (provider == "openrouter")
            {
                request.Headers.Add("HTTP-Referer", "https://pos.local");
                request.Headers.Add("X-Title", "CommerceAI");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var bodyText = await ReadBoundedAsync(response.Content, timeout.Token);
                    var content = ExtractContent(bodyText);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        using var document = JsonDocument.Parse(bodyText);
                        var tokens = document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                            usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
                        return new(content, tokens);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
                {
                    throw new AIProviderException(502, "Invalid AI response", "The AI returned an empty, incomplete or invalid response. Please try again.");
                }
                throw new AIProviderException(502, "Invalid AI response", "The AI returned an empty, incomplete or invalid response. Please try again.");
            }

            var status = (int)response.StatusCode;
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            var retrySeconds = (int)Math.Min(86400, Math.Ceiling(delay.TotalSeconds));
            // One retry only, within the original timeout; never retry invalid requests or auth.
            if (attempt == 0 && (status is 500 or 502 or 503 or 504 || status == 429) && delay <= TimeSpan.FromSeconds(2))
            {
                await Task.Delay(delay, timeout.Token);
                if (admitRetry != null)
                {
                    var admission = await admitRetry();
                    if (!admission.Allowed)
                        throw new AIProviderException(429, "AI rate limited", "The AI service is busy. Please wait before trying again.", admission.RetryAfterSeconds);
                }
                continue;
            }
            throw status switch
            {
                429 => new AIProviderException(429, "AI rate limited", "The AI service is busy. Please wait before trying again.", retrySeconds, status),
                401 or 403 => new AIProviderException(503, "AI authentication failed", "The AI provider rejected the server credential or its permissions. Check CommerceAI:ApiKey securely on the server.", providerStatus: status),
                402 => new AIProviderException(503, "AI provider credit required", "The AI provider requires account credit. Contact the store administrator.", providerStatus: status),
                400 or 404 or 422 => new AIProviderException(503, "AI model unavailable", "The configured AI model cannot handle this request. Please contact the store administrator.", providerStatus: status),
                _ => new AIProviderException(502, "AI provider error", "The AI service is temporarily unavailable. Please try again.", providerStatus: status)
            };
        }
    }

    internal static string? ExtractContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)) return null;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is "length" or "content_filter" or "error")) return null;
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Array)
            return string.Concat(content.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("type", out var type) && type.GetString() == "text")
                .Select(p => p.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null));
        return null;
    }

    internal static bool ValidConfiguration(string endpoint, string model, string key, int timeoutSeconds, int maxTokens, double temperature) =>
        !string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(key) &&
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") && string.IsNullOrEmpty(uri.UserInfo) &&
        timeoutSeconds is >= 1 and <= 120 && maxTokens is >= 1 and <= 32768 && double.IsFinite(temperature) && temperature is >= 0 and <= 1;

    internal static bool WithinPromptBudget(string system, string user) => system.Length + user.Length <= 65536;

    internal static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int limit = 131072;
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Provider response exceeds limit");
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Provider response exceeds limit");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

internal sealed record AICompletion(string Content, int TokensUsed);

internal sealed class AIProviderException(int statusCode, string code, string userMessage, int? retryAfterSeconds = null, int? providerStatus = null) : Exception(userMessage)
{
    internal int StatusCode { get; } = statusCode;
    internal string Code { get; } = code;
    internal int? RetryAfterSeconds { get; } = retryAfterSeconds;
    internal int? ProviderStatus { get; } = providerStatus;
}
