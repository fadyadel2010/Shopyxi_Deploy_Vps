using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.API.DTOs.Admin;
using PointOfSale.API.Controllers;
using PointOfSale.API.Services.AI;
using PointOfSale.API.Services.AI.Admin;
using PointOfSale.API.Services.AI.Models;
using PointOfSale.API.Services.AI.Router;
using PointOfSale.API.Services.Payments;
using PointOfSale.Infrastructure.MongoDB.Documents;
using PointOfSale.Infrastructure.MongoDB.Services;
using PointOfSale.Infrastructure.MultiTenancy;

namespace PointOfSale.Tests.Security;

public sealed class ShopixyAiReliabilityTests
{
    private static CommerceAIOptions Config() => new() { Enabled = true, Model = "openrouter/free", ApiKey = "synthetic-server-only", MaxTokens = 8192, TimeoutSeconds = 90 };
    private static Mock<IAIRateLimiter> Limiter()
    {
        var mock = new Mock<IAIRateLimiter>();
        mock.Setup(x => x.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(RateLimitResult.CreateAllowed(1, 10));
        return mock;
    }
    private static AdminProductGeneratorService Generator(HttpMessageHandler handler, CommerceAIOptions? options = null) =>
        new(new HttpClient(handler), Options.Create(options ?? Config()), Limiter().Object, NullLogger<AdminProductGeneratorService>.Instance);
    private static string Product(int features = 5, int faqs = 3) => JsonSerializer.Serialize(new {
        name = "iPhone 18", shortDescription = "Unverified draft", description = "Draft for merchant review; specifications are not verified.", brand = "", manufacturer = "",
        tags = new[] { "phone" }, metaTitle = "iPhone 18 draft", metaDescription = "Review before publication", metaKeywords = new[] { "phone" }, slug = "iPhone 18",
        features = Enumerable.Range(1, features).Select(i => $"Draft feature {i}; verify"), faq = Enumerable.Range(1, faqs).Select(i => new { question = $"Question {i}?", answer = "Please verify with the merchant." })
    });
    private static HttpResponseMessage Completion(string content, string finish = "stop") => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content } } } }))
    };

    [Fact]
    public async Task Product_ConstructsFreeStructuredRequestAndMapsActualCounts()
    {
        var handler = new Phase7ExecutionTests.Handler(async (request, ct) => {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = json.RootElement;
            Assert.Equal("openrouter/free", root.GetProperty("model").GetString());
            Assert.Equal(8192, root.GetProperty("max_tokens").GetInt32());
            Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
            Assert.True(root.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
            Assert.False(root.TryGetProperty("reasoning", out _));
            Assert.Contains("iPhone 18", root.GetProperty("messages")[1].GetProperty("content").GetString());
            return Completion("```json\n" + Product() + "\n```");
        });
        var result = await Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "iPhone 18" });
        Assert.Equal("iPhone 18", result.Name); Assert.Equal("iphone-18", result.Slug);
        Assert.Equal(5, result.Features.Count); Assert.Equal(3, result.Faq.Count); Assert.Empty(result.Brand);
    }

    [Theory]
    [InlineData(401, 503, 1)] [InlineData(403, 503, 1)] [InlineData(402, 503, 1)]
    [InlineData(400, 503, 1)] [InlineData(404, 503, 1)] [InlineData(422, 503, 1)]
    [InlineData(429, 429, 2)] [InlineData(500, 502, 2)] [InlineData(502, 502, 2)] [InlineData(503, 502, 2)]
    public async Task Product_ProviderErrorsAreMappedAndRetriesBounded(int status, int expected, int attempts)
    {
        var calls = 0;
        var handler = new Phase7ExecutionTests.Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET_MUST_NOT_ESCAPE") }); });
        var exception = await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }));
        Assert.Equal(expected, exception.StatusCode); Assert.Equal(attempts, calls); Assert.DoesNotContain("SECRET", exception.Message);
    }

    [Fact]
    public async Task Product_LongRetryAfterIsNotRetriedAndPreserved()
    {
        var calls = 0;
        var handler = new Phase7ExecutionTests.Handler((_, _) => { calls++; var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(45)); return Task.FromResult(r); });
        var ex = await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }));
        Assert.Equal(45, ex.RetryAfterSeconds); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Product_TransientFailureCanRecoverOnce()
    {
        var calls = 0;
        var handler = new Phase7ExecutionTests.Handler((_, _) => Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Completion(Product())));
        Assert.Equal(5, (await Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" })).Features.Count); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("missing-key")] [InlineData("paid-model")] [InlineData("invalid-endpoint")] [InlineData("disabled")]
    public async Task Product_InvalidConfigurationCannotContactProvider(string scenario)
    {
        var options = Config();
        switch (scenario) { case "missing-key": options.ApiKey = ""; break; case "paid-model": options.Model = "deepseek/deepseek-chat"; break; case "invalid-endpoint": options.Endpoint = "invalid"; break; default: options.Enabled = false; break; }
        var calls = 0;
        var handler = new Phase7ExecutionTests.Handler((_, _) => { calls++; return Task.FromResult(Completion(Product())); });
        var ex = await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler, options).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }));
        Assert.Equal(503, ex.StatusCode); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Product_TesterEnvironmentModelOverrideReportsPolicyConflict_NotMissingCredential()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["CommerceAI:Enabled"] = "true", ["CommerceAI:Provider"] = "openrouter",
            ["CommerceAI:Model"] = "deepseek/deepseek-chat", ["CommerceAI:FreeModelsOnly"] = "true",
            ["CommerceAI:ApiKey"] = "synthetic-do-not-echo", ["CommerceAI:TimeoutSeconds"] = "30",
            ["CommerceAI:MaxTokens"] = "1000"
        }).Build().GetSection("CommerceAI").Get<CommerceAIOptions>()!;
        var calls = 0;
        var handler = new Phase7ExecutionTests.Handler((_, _) => { calls++; return Task.FromResult(Completion(Product())); });
        var ex = await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler, config).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }));
        Assert.Equal("AI model policy conflict", ex.ErrorCode);
        Assert.Contains("CommerceAI:FreeModelsOnly", ex.UserMessage);
        Assert.DoesNotContain(config.ApiKey, ex.UserMessage);
        Assert.Equal(0, calls);
        // Policy is an explicit operator choice, not a silent model/provider fallback.
        config.FreeModelsOnly = false;
        Assert.Equal(5, (await Generator(handler, config).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" })).Features.Count);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(" openrouter ", "Bearer")]
    [InlineData("openai", "Bearer")]
    [InlineData("local", "Bearer")]
    [InlineData("azure", "api-key")]
    public async Task Product_SupportedProviderContractUsesConfiguredEndpointWithoutFallback(string provider, string auth)
    {
        var config = Config(); config.Provider = provider; config.Endpoint = "https://ai.example.test/chat/completions?api-version=2024-10-21";
        var handler = new Phase7ExecutionTests.Handler((request, _) => {
            Assert.Equal(config.Endpoint, request.RequestUri!.AbsoluteUri);
            if (auth == "api-key") { Assert.True(request.Headers.Contains("api-key")); Assert.Null(request.Headers.Authorization); }
            else Assert.Equal(auth, request.Headers.Authorization!.Scheme);
            return Task.FromResult(Completion(Product()));
        });
        Assert.Equal(5, (await Generator(handler, config).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" })).Features.Count);
    }

    [Theory]
    [InlineData("unsupported", "AI provider unsupported", "CommerceAI:Provider")]
    [InlineData("missing-key", "AI configuration missing", "CommerceAI:ApiKey")]
    [InlineData("endpoint", "AI configuration invalid", "CommerceAI:Endpoint")]
    public async Task Product_DiagnosticsAreActionableAndDoNotEchoConfiguration(string scenario, string code, string key)
    {
        var config = Config();
        if (scenario == "unsupported") config.Provider = "secret-provider-do-not-echo";
        if (scenario == "missing-key") config.ApiKey = "";
        if (scenario == "endpoint") config.Endpoint = "https://secret:credential@ai.example.test";
        var handler = new Phase7ExecutionTests.Handler((_, _) => throw new InvalidOperationException("must not contact upstream"));
        var ex = await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler, config).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }));
        Assert.Equal(code, ex.ErrorCode); Assert.Contains(key, ex.UserMessage); Assert.DoesNotContain("secret", ex.UserMessage);
    }

    [Fact]
    public async Task Product_UpstreamAuthenticationAndNetworkFailureAreDistinct()
    {
        var auth = new Phase7ExecutionTests.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var network = new Phase7ExecutionTests.Handler((_, _) => throw new HttpRequestException("synthetic endpoint detail"));
        Assert.Equal("AI authentication failed", (await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(auth).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }))).ErrorCode);
        var ex = await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(network).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }));
        Assert.Equal(502, ex.StatusCode); Assert.Equal("AI request failed", ex.ErrorCode); Assert.DoesNotContain("endpoint detail", ex.UserMessage);
    }

    [Theory]
    [InlineData("{}")] [InlineData("null")] [InlineData("[]")] [InlineData("not json")]
    public async Task Product_MalformedOutputDoesNotProducePartialFields(string content)
    {
        var handler = new Phase7ExecutionTests.Handler((_, _) => Task.FromResult(Completion(content)));
        Assert.Equal(502, (await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }))).StatusCode);
    }

    [Theory]
    [InlineData("length")] [InlineData("content_filter")] [InlineData("error")]
    public async Task Product_IncompleteCompletionIsRejected(string reason)
    {
        var handler = new Phase7ExecutionTests.Handler((_, _) => Task.FromResult(Completion(Product(), reason)));
        Assert.Equal(502, (await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }))).StatusCode);
    }

    [Fact]
    public async Task Product_CountMismatchCannotPopulateForm()
    {
        var handler = new Phase7ExecutionTests.Handler((_, _) => Task.FromResult(Completion(Product(4, 2))));
        Assert.Equal(502, (await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }))).StatusCode);
    }

    [Fact]
    public async Task Product_TimeoutIs504AndCallerCancellationPropagates()
    {
        var options = Config(); options.TimeoutSeconds = 1;
        var handler = new Phase7ExecutionTests.Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Completion(Product()); });
        Assert.Equal(504, (await Assert.ThrowsAsync<AdminProductGenerationException>(() => Generator(handler, options).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }))).StatusCode);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator(handler).GenerateProductAsync(Guid.NewGuid(), new() { ProductIdea = "phone" }, cts.Token));
    }

    [Fact]
    public void Configuration_BindsExistingSectionAndFreePolicy()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CommerceAI:Model"] = "openrouter/free", ["CommerceAI:FreeModelsOnly"] = "true", ["CommerceAI:TimeoutSeconds"] = "90", ["CommerceAI:MaxTokens"] = "8192" }).Build();
        var options = configuration.GetSection(CommerceAIOptions.SectionName).Get<CommerceAIOptions>()!;
        Assert.True(options.FreeModelsOnly); Assert.Equal(8192, options.MaxTokens); Assert.Equal(90, options.TimeoutSeconds);
    }

    [Theory]
    [InlineData("Tell me about this phone", true, true, false)]
    [InlineData("Don't buy this phone", true, true, false)]
    [InlineData("I want to buy it", true, true, true)]
    [InlineData("عايز أشتري الموبايل", true, true, true)]
    [InlineData("I want to buy it", false, true, false)]
    [InlineData("I want to buy it", true, false, false)]
    public void Commerce_DraftsRequireCurrentPurchaseIntentAndAvailableExactVariant(string message, bool stock, bool variantStock, bool expected)
    {
        var id = Guid.NewGuid(); var variant = Guid.NewGuid();
        var context = new CommerceContext { Message = message, Products = [new() { Id = id, InStock = stock, HasVariants = true, Variants = [new() { Id = variant, InStock = variantStock }] }] };
        var json = JsonSerializer.Serialize(new { type = "order_draft", message = "Review in checkout", orderDraft = new { intent = "create", items = new[] { new { productId = id, variantId = variant, quantity = 1 } } } });
        Assert.Equal(expected, new AIResponseValidator(NullLogger<AIResponseValidator>.Instance).TryValidate(json, context, out _, out _));
    }

    [Fact]
    public void Commerce_ModelCannotGenerateArbitraryNavigation()
    {
        var validator = new AIResponseValidator(NullLogger<AIResponseValidator>.Instance);
        Assert.False(validator.TryValidate("{\"type\":\"message\",\"message\":\"Buy at https://evil.example\"}", new(), out _, out _));
    }

    [Fact]
    public void Commerce_DraftMessageAndProductNamesAreServerOwnedNotModelSideEffectClaims()
    {
        var id = Guid.NewGuid(); var context = new CommerceContext { Message = "I want to buy it", Products = [new() { Id = id, Name = "Trusted phone", InStock = true }] };
        var json = JsonSerializer.Serialize(new { type = "order_draft", message = "I charged your card and added the order", orderDraft = new { intent = "create", items = new[] { new { productId = id, productName = "FORGED", quantity = 1 } } } });
        Assert.True(new AIResponseValidator(NullLogger<AIResponseValidator>.Instance).TryValidate(json, context, out var parsed, out _));
        Assert.Contains("No cart changes or order have been made", parsed!.Message);
        Assert.Equal("Trusted phone", parsed.OrderDraft!.Items[0].ProductName); Assert.DoesNotContain("charged", parsed.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Commerce_DisabledOrMissingTenantConfigurationNeverCallsAi(bool hasDisabledConfig)
    {
        var tenant = Guid.NewGuid(); await using var db = Phase7ProviderTests.Context(tenant);
        if (hasDisabledConfig) { db.StoreConfigs.Add(new() { Id = Guid.NewGuid(), TenantId = tenant, CommerceAiEnabled = false }); await db.SaveChangesAsync(); }
        var router = new Mock<IAIRouterService>(MockBehavior.Strict);
        var tenantContext = new TenantContext(); tenantContext.SetTenantId(tenant);
        var controller = new StorefrontChatController(Mock.Of<ICommerceContextBuilder>(), router.Object, db, tenantContext, NullLogger<StorefrontChatController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.ProcessMessage(new() { Message = "hello" })).StatusCode);
        router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Commerce_PaymentContextUsesTenantAndExistingRuntimeAdmission()
    {
        var tenant = Guid.NewGuid(); await using var db = Phase7ProviderTests.Context(tenant);
        var repo = new Mock<IPaymentMethodReadRepository>();
        repo.Setup(x => x.GetEnabledPaymentMethodsAsync(tenant, default)).ReturnsAsync([
            new() { TenantId = tenant, Code = "COD", DisplayName = "Cash", IsEnabled = true, IsConfigurationValid = true },
            new() { TenantId = tenant, Code = "FAWRY", IsEnabled = true, IsConfigurationValid = true },
            new() { TenantId = Guid.NewGuid(), Code = "STRIPE", DisplayName = "FOREIGN", IsEnabled = true, IsConfigurationValid = true }
        ]);
        var readiness = new Mock<IStorefrontPaymentRuntimeReadinessService>();
        readiness.Setup(x => x.EvaluateAsync(tenant, "COD", default)).ReturnsAsync(StorefrontPaymentRuntimeReadiness.Ready);
        readiness.Setup(x => x.EvaluateAsync(tenant, "FAWRY", default)).ReturnsAsync(StorefrontPaymentRuntimeReadiness.Blocked("unavailable"));
        var builder = new CommerceContextBuilder(null!, null!, null!, db, null!, Options.Create(Config()), NullLogger<CommerceContextBuilder>.Instance, repo.Object, readiness.Object);
        var method = typeof(CommerceContextBuilder).GetMethod("BuildPaymentContextAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<List<PaymentMethodContext>>)method.Invoke(builder, [tenant])!;
        Assert.Equal("COD", Assert.Single(result).Code);
        readiness.Verify(x => x.EvaluateAsync(tenant, "STRIPE", default), Times.Never);
    }
}
