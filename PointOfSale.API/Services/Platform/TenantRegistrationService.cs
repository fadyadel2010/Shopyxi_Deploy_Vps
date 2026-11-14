using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using PointOfSale.API.Configuration;
using PointOfSale.API.DTOs.Platform;
using PointOfSale.API.Middleware;
using PointOfSale.API.Models;
using PointOfSale.API.Security;
using PointOfSale.API.Services.Auth;
using PointOfSale.API.Services.Governance;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.Events;
using PointOfSale.Core.Interfaces;
using PointOfSale.Core.Interfaces.Accounting;
using PointOfSale.Core.Security;
using PointOfSale.Core.Tenancy;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.API.Services.Platform;

/// <summary>
/// Service for tenant registration with atomic bootstrap.
/// Phase 8.0: Tenant Registration System
/// 
/// CRITICAL: All bootstrap steps execute in a single transaction.
/// Any failure results in complete rollback - no partial tenants.
/// </summary>
public interface ITenantRegistrationService
{
    /// <summary>
    /// Registers a new tenant with atomic bootstrap.
    /// Creates all required entities in a single transaction.
    /// </summary>
    Task<TenantRegistrationResult> RegisterTenantAsync(TenantRegistrationRequest request, CancellationToken cancellationToken = default);

    Task<TenantRegistrationResult> RegisterTenantAsync(
        TenantRegistrationRequest request,
        string? requestId,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if a subdomain is available.
    /// </summary>
    Task<SubdomainCheckResponse> CheckSubdomainAvailabilityAsync(string subdomain, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks if an email is already registered.
    /// </summary>
    Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of tenant registration operation
/// </summary>
public class TenantRegistrationResult
{
    public bool Success { get; set; }
    public TenantRegistrationResponse? Response { get; set; }
    public RegistrationErrorResponse? Error { get; set; }
}

public class TenantRegistrationService : ITenantRegistrationService
{
    private readonly ApplicationDbContext _context;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly EmailVerificationSettings _emailSettings;
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<TenantRegistrationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IUserGovernanceService _userGovernanceService;
    private readonly IEventPublisher _eventPublisher;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly StorefrontUrlResolver _storefrontUrlResolver;

    public TenantRegistrationService(
        ApplicationDbContext context,
        IPasswordHasher<User> passwordHasher,
        IOptions<EmailVerificationSettings> emailSettings,
        IOptions<JwtSettings> jwtSettings,
        ILogger<TenantRegistrationService> logger,
        IConfiguration configuration,
        IUserGovernanceService userGovernanceService,
        IEventPublisher eventPublisher,
        IHttpContextAccessor httpContextAccessor,
        StorefrontUrlResolver storefrontUrlResolver)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _emailSettings = emailSettings.Value;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
        _configuration = configuration;
        _userGovernanceService = userGovernanceService;
        _eventPublisher = eventPublisher;
        _httpContextAccessor = httpContextAccessor;
        _storefrontUrlResolver = storefrontUrlResolver;
    }

    public Task<TenantRegistrationResult> RegisterTenantAsync(
        TenantRegistrationRequest request,
        CancellationToken cancellationToken = default) =>
        RegisterTenantAsync(request, requestId: null, cancellationToken);

    public async Task<TenantRegistrationResult> RegisterTenantAsync(
        TenantRegistrationRequest request,
        string? requestId,
        CancellationToken cancellationToken = default)
    {
        if (!StoreSlug.TryNormalize(request.Subdomain, out var normalizedSubdomain, out var subdomainError))
        {
            return CreateError("INVALID_SUBDOMAIN", subdomainError!, "subdomain");
        }

        var normalizedEmail = request.Email.ToLowerInvariant().Trim();
        var normalizedRequestId = string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim();
        if (normalizedRequestId?.Length > 200)
        {
            return CreateError(
                "IDEMPOTENCY_KEY_INVALID",
                "X-Request-Id must not exceed 200 characters.");
        }

        var requestFingerprint = BuildRequestFingerprint(request, normalizedEmail, normalizedSubdomain);
        var requestIdHash = HashValue(normalizedRequestId == null
            ? $"tuple:{normalizedEmail}\n{normalizedSubdomain}"
            : $"client:{normalizedRequestId}");

        var existingOperation = await _context.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                operation => operation.IdempotencyKeyHash == requestIdHash,
                cancellationToken);

        if (existingOperation != null)
        {
            return await ReplayRegistrationAsync(
                existingOperation,
                requestFingerprint,
                request.Password,
                cancellationToken);
        }
        
        _logger.LogInformation("Starting tenant registration for {Email}, subdomain: {Subdomain}", 
            normalizedEmail, normalizedSubdomain);

        try
        {
            await _userGovernanceService.EnsureRegistrationAllowedAsync(
                normalizedEmail,
                phoneNumber: null,
                ipAddress: null,
                deviceFingerprint: null,
                cancellationToken);
        }
        catch (UserGovernanceException exception)
        {
            _logger.LogWarning(
                exception,
                "Tenant registration blocked by governance policy for email {Email}",
                normalizedEmail);
            return CreateError(exception.Code, "An unexpected error occurred", "email");
        }

        // =====================================================================
        // PRE-VALIDATION (before transaction)
        // =====================================================================
        
        // Check email uniqueness (server-authoritative normalized key)
        var normalizedIdentityEmail = PointOfSale.Core.Security.PosIdentityNormalizer.NormalizeEmail(request.Email);
        var emailExists = await _context.Users
            .AnyAsync(u => u.NormalizedEmail == normalizedIdentityEmail, cancellationToken);
        if (emailExists)
        {
            return CreateError("EMAIL_TAKEN", 
                "This email is already registered. Sign in instead?", "email");
        }
        
        // Check subdomain uniqueness
        var subdomainExists = await _context.Tenants
            .AnyAsync(t => t.Slug != null && t.Slug.ToLower() == normalizedSubdomain, cancellationToken);
        if (subdomainExists)
        {
            var suggestions = SubdomainValidator.GenerateSuggestions(normalizedSubdomain);
            // Filter out suggestions that are also taken
            var availableSuggestions = new List<string>();
            foreach (var suggestion in suggestions)
            {
                var taken = await _context.Tenants
                    .AnyAsync(t => t.Slug != null && t.Slug.ToLower() == suggestion, cancellationToken);
                if (!taken)
                    availableSuggestions.Add(suggestion);
            }
            
            return new TenantRegistrationResult
            {
                Success = false,
                Error = new RegistrationErrorResponse
                {
                    Error = "SUBDOMAIN_TAKEN",
                    Message = "This address is already taken",
                    FieldErrors = new Dictionary<string, string> { { "subdomain", "This address is already taken" } },
                    Suggestions = availableSuggestions
                }
            };
        }

        // =====================================================================
        // ATOMIC BOOTSTRAP (single transaction)
        // =====================================================================
        
        // Validate currency - only EGP is supported
        if (!string.IsNullOrWhiteSpace(request.Currency) && 
            !string.Equals(request.Currency.Trim(), "EGP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Tenant registration attempted with non-EGP currency: {Currency} for email {Email}. Only EGP is supported.",
                request.Currency,
                normalizedEmail);
            return CreateError("INVALID_CURRENCY", 
                "Only EGP currency is supported at this time.", "currency");
        }

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        
        try
        {
            var now = DateTime.UtcNow;
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            
            // -----------------------------------------------------------------
            // Step 1: Create Tenant (status: PENDING_SETUP)
            // -----------------------------------------------------------------
            var tenant = new Tenant
            {
                Id = tenantId,
                Name = request.BusinessName.Trim(),
                Slug = normalizedSubdomain,
                ContactEmail = normalizedEmail,
                IsActive = true,
                Status = TenantStatus.PendingSetup,
                Country = request.Country,
                Currency = "EGP", // HARDENED: Only EGP is supported
                BootstrapVersion = 1,
                CreatedAtUtc = now
            };
            _context.Tenants.Add(tenant);
            
            _logger.LogDebug("Step 1: Created tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 2: Create Primary Admin User (role: OWNER)
            // -----------------------------------------------------------------
            var user = new User
            {
                Id = userId,
                TenantId = tenantId,
                Username = normalizedEmail, // Email as username
                Email = normalizedEmail,
                NormalizedUsername = PointOfSale.Core.Security.PosIdentityNormalizer.NormalizeUsername(normalizedEmail),
                NormalizedEmail = PointOfSale.Core.Security.PosIdentityNormalizer.NormalizeEmail(normalizedEmail),
                FirstName = request.BusinessName.Split(' ').FirstOrDefault() ?? "Owner",
                LastName = "",
                Role = UserRole.Owner,
                IsActive = true,
                CreatedAt = now,
                // Phase 8.4: New users are unverified by default
                EmailVerificationStatus = _emailSettings.Enabled 
                    ? EmailVerificationStatus.Unverified 
                    : EmailVerificationStatus.Verified,
                EmailVerifiedAtUtc = _emailSettings.Enabled ? null : now
            };
            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
            _context.Users.Add(user);
            
            _logger.LogDebug("Step 2: Created user {UserId} for tenant {TenantId}", userId, tenantId);
            
            // -----------------------------------------------------------------
            // Step 3: Create StoreConfig (StorefrontStatus = disabled)
            // -----------------------------------------------------------------
            var storeConfig = new StoreConfig
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StoreName = request.BusinessName.Trim(),
                StoreDescription = null,
                ThemeId = "default",
                StorefrontStatus = "disabled", // CRITICAL: Disabled by default
                Theme = new StoreThemeConfig
                {
                    Colors = new ColorsConfig
                    {
                        Primary = "#2563eb",
                        Secondary = "#0f172a",
                        Background = "#ffffff",
                        Text = "#020617",
                        Muted = "#64748b"
                    },
                    Font = new FontConfig
                    {
                        Family = "Inter",
                        BaseSize = "16px"
                    },
                    Ui = new UiConfig
                    {
                        BorderRadius = "8px",
                        ButtonStyle = "solid"
                    },
                    Layout = new LayoutConfig
                    {
                        ProductCardVariant = "default",
                        HeaderVariant = "standard"
                    }
                },
                Behavior = new StoreBehaviorConfig
                {
                    EnableGuestCheckout = true,
                    ShowOutOfStockProducts = false,
                    EnableSearch = true
                },
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _context.StoreConfigs.Add(storeConfig);
            
            _logger.LogDebug("Step 3: Created store config for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 4: Create Default Warehouse
            // -----------------------------------------------------------------
            var warehouse = new Warehouse
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Main Warehouse",
                Address = "",
                IsDefault = true,
                IsActive = true,
                CreatedAt = now
            };
            _context.Warehouses.Add(warehouse);
            
            _logger.LogDebug("Step 4: Created default warehouse for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 5: Default Payment Methods (COD enabled via shipping methods)
            // Note: COD is enabled by default in shipping methods configuration
            // -----------------------------------------------------------------
            // No separate payment method entity - payment is handled at order level
            _logger.LogDebug("Step 5: Payment methods configured (COD default)");
            
            // -----------------------------------------------------------------
            // Step 6: Create Root Category
            // -----------------------------------------------------------------
            var category = new Category
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Uncategorized",
                Description = "Default category for new products",
                ParentCategoryId = null,
                CreatedAt = now
            };
            _context.Categories.Add(category);
            
            _logger.LogDebug("Step 6: Created root category for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 7: Create Default Financial Accounts
            // Phase: Tenant Isolation Hardening - no global seed data
            // -----------------------------------------------------------------
            var cashAccount = new FinancialAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                AccountCode = "1100",
                Name = "Cash",
                AccountNumber = "CASH-001",
                AccountType = PointOfSale.Core.Enums.AccountType.Asset,
                Level = 1,
                IsSystem = true,
                Balance = 0,
                IsDeleted = false,
                CreatedAt = now
            };
            _context.FinancialAccounts.Add(cashAccount);
            
            var visaAccount = new FinancialAccount
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                AccountCode = "1200",
                Name = "Visa Account",
                AccountNumber = "VISA-001",
                AccountType = PointOfSale.Core.Enums.AccountType.Asset,
                Level = 1,
                IsSystem = true,
                Balance = 0,
                IsDeleted = false,
                CreatedAt = now
            };
            _context.FinancialAccounts.Add(visaAccount);
            
            _logger.LogDebug("Step 7: Created default financial accounts for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 8: Create Default Shipping Methods
            // Phase: Tenant Isolation Hardening - no global seed data
            // -----------------------------------------------------------------
            var standardShipping = new ShippingMethod
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Standard Shipping",
                NameAr = "شحن عادي",
                Description = "Standard delivery within 5-7 business days",
                BaseRate = 10.00m,
                PerKgRate = 0,
                EstimatedDeliveryDaysMin = 5,
                EstimatedDeliveryDaysMax = 7,
                FulfillmentWarehouseId = warehouse.Id,
                IsActive = true,
                SortOrder = 1,
                IsDeleted = false,
                CreatedAt = now
            };
            _context.ShippingMethods.Add(standardShipping);
            
            var expressShipping = new ShippingMethod
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Express Shipping",
                NameAr = "شحن سريع",
                Description = "Express delivery within 2-3 business days",
                BaseRate = 25.00m,
                PerKgRate = 2.00m,
                EstimatedDeliveryDaysMin = 2,
                EstimatedDeliveryDaysMax = 3,
                FulfillmentWarehouseId = warehouse.Id,
                IsActive = true,
                SortOrder = 2,
                IsDeleted = false,
                CreatedAt = now
            };
            _context.ShippingMethods.Add(expressShipping);
            
            var freeShipping = new ShippingMethod
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Free Shipping",
                NameAr = "شحن مجاني",
                Description = "Free shipping for orders over 100",
                BaseRate = 0,
                PerKgRate = 0,
                FreeShippingThreshold = 100.00m,
                EstimatedDeliveryDaysMin = 7,
                EstimatedDeliveryDaysMax = 14,
                FulfillmentWarehouseId = warehouse.Id,
                IsActive = true,
                SortOrder = 3,
                IsDeleted = false,
                CreatedAt = now
            };
            _context.ShippingMethods.Add(freeShipping);
            
            _logger.LogDebug("Step 8: Created default shipping methods for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 9: Create Default Unit
            // Phase: Tenant Isolation Hardening - no global seed data
            // -----------------------------------------------------------------
            var defaultUnit = new Unit
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Piece",
                Symbol = "pc",
                Description = "Default unit of measure",
                IsActive = true,
                IsDeleted = false,
                CreatedAt = now
            };
            _context.Units.Add(defaultUnit);
            
            _logger.LogDebug("Step 9: Created default unit for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 10: Create Onboarding Checklist
            // -----------------------------------------------------------------
            var checklist = new OnboardingChecklist
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ItemsJson = OnboardingChecklist.GetDefaultItemsJson(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _context.OnboardingChecklists.Add(checklist);
            
            _logger.LogDebug("Step 10: Created onboarding checklist for tenant {TenantId}", tenantId);
            
            // -----------------------------------------------------------------
            // Step 11: Finalize Tenant State → ACTIVE
            // -----------------------------------------------------------------
            tenant.SetLifecycleStatus(TenantStatus.Active);
            
            _logger.LogDebug("Step 11: Tenant {TenantId} status set to ACTIVE", tenantId);
            
            // Persist the session binding inside the bootstrap transaction. A
            // successful registration response must never point at a tenant
            // whose owner session failed to persist after commit.
            // Resolve URL authority before commit as well, so configuration
            // failure cannot leave a committed tenant behind a failed response.
            var storefrontUrl = _storefrontUrlResolver.Resolve(tenant);
            var (accessToken, refreshToken) = GenerateTokens(user, tenantId);

            var provisioningOperation = new TenantProvisioningOperation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                OwnerUserId = userId,
                StoreConfigId = storeConfig.Id,
                IdempotencyKeyHash = requestIdHash,
                RequestFingerprint = requestFingerprint,
                CanonicalHostname = new Uri(storefrontUrl).Host.ToLowerInvariant(),
                State = TenantProvisioningState.Provisioning,
                AttemptCount = 0,
                NextAttemptAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _context.TenantProvisioningOperations.Add(provisioningOperation);

            // The EF bus outbox persists this request in the same transaction as
            // Tenant, Owner, StoreConfig, and the provisioning operation.
            await _eventPublisher.PublishAsync(new TenantProvisioningRequestedEvent
            {
                EventId = provisioningOperation.Id,
                TenantId = tenantId,
                IdempotencyKey = $"tenant-provisioning:{provisioningOperation.Id:N}",
                ProvisioningOperationId = provisioningOperation.Id,
                OwnerUserId = userId,
                StoreConfigId = storeConfig.Id,
                CanonicalHostname = provisioningOperation.CanonicalHostname
            }, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            await _userGovernanceService.EnsureProfileExistsAsync(
                UserGovernanceRole.Vendor,
                tenantId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();

            _logger.LogInformation("Tenant registration completed successfully: {TenantId} ({Subdomain})", 
                tenantId, normalizedSubdomain);
            
            // Build response URLs from the same authority used by every other
            // storefront link and by host-based tenant resolution.
            var posUrl = _configuration["Platform:PosUrl"] ?? "https://pos.myplatform.com";
            
            return new TenantRegistrationResult
            {
                Success = true,
                Response = new TenantRegistrationResponse
                {
                    TenantId = tenantId,
                    Subdomain = normalizedSubdomain,
                    StoreName = request.BusinessName.Trim(),
                    StorefrontUrl = storefrontUrl,
                    PosUrl = posUrl,
                    Status = "provisioning",
                    Message = "Your store has been created and provisioning is in progress.",
                    User = new RegistrationUserDto
                    {
                        Id = userId,
                        Email = normalizedEmail,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        Role = "Owner",
                        TenantId = tenantId
                    },
                    AccessToken = accessToken,
                    RefreshToken = refreshToken
                }
            };
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "IX_Tenants_Slug"))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            var replay = await TryReplayAfterConflictAsync(
                requestIdHash,
                requestFingerprint,
                request.Password,
                cancellationToken);
            if (replay != null)
            {
                return replay;
            }

            return CreateSubdomainTakenError(normalizedSubdomain);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "UX_Tenants_Slug_CaseInsensitive"))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            var replay = await TryReplayAfterConflictAsync(
                requestIdHash,
                requestFingerprint,
                request.Password,
                cancellationToken);
            return replay ?? CreateSubdomainTakenError(normalizedSubdomain);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "UQ_TenantProvisioningOperations_IdempotencyKeyHash"))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            var replay = await TryReplayAfterConflictAsync(
                requestIdHash,
                requestFingerprint,
                request.Password,
                cancellationToken);
            return replay ?? CreateError(
                "REGISTRATION_FAILED",
                "We couldn't create your store right now. Please try again.");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "IX_Users_NormalizedEmail"))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            var replay = await TryReplayAfterConflictAsync(
                requestIdHash,
                requestFingerprint,
                request.Password,
                cancellationToken);
            if (replay != null)
            {
                return replay;
            }

            return CreateError("EMAIL_TAKEN", "This email is already registered. Sign in instead?", "email");
        }
        catch (Exception ex)
        {
            var correlationId = GetCorrelationId();
            _logger.LogError(
                ex,
                "Tenant registration failed, rolling back transaction | CorrelationId: {CorrelationId} | ExceptionType: {ExceptionType} | ExceptionMessage: {ExceptionMessage} | InnerExceptionType: {InnerExceptionType} | InnerExceptionMessage: {InnerExceptionMessage} | StackTrace: {StackTrace}",
                correlationId,
                ex.GetType().FullName,
                ex.Message,
                ex.InnerException?.GetType().FullName,
                ex.InnerException?.Message,
                ex.StackTrace);
            await transaction.RollbackAsync(cancellationToken);
            
            return CreateError("REGISTRATION_FAILED", 
                "We couldn't create your store right now. Please try again.");
        }
    }

    private string GetCorrelationId()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        return httpContext == null
            ? System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString("N")
            : RequestContextMetadata.GetCorrelationId(httpContext);
    }

    private async Task<TenantRegistrationResult?> TryReplayAfterConflictAsync(
        string requestIdHash,
        string requestFingerprint,
        string password,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        var operation = await _context.TenantProvisioningOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.IdempotencyKeyHash == requestIdHash,
                cancellationToken);

        return operation == null
            ? null
            : await ReplayRegistrationAsync(
                operation,
                requestFingerprint,
                password,
                cancellationToken);
    }

    private async Task<TenantRegistrationResult> ReplayRegistrationAsync(
        TenantProvisioningOperation operation,
        string requestFingerprint,
        string password,
        CancellationToken cancellationToken)
    {
        if (!FixedTimeHexEquals(operation.RequestFingerprint, requestFingerprint))
        {
            return CreateError(
                "IDEMPOTENCY_KEY_REUSED",
                "X-Request-Id was already used for a different registration request.");
        }

        var authority = await _context.Tenants
            .IgnoreQueryFilters()
            .Where(tenant =>
                tenant.Id == operation.TenantId &&
                tenant.DeletedAtUtc == null &&
                tenant.IsActive &&
                tenant.Status == TenantStatus.Active)
            .Join(
                _context.Users.IgnoreQueryFilters().Where(user =>
                    user.Id == operation.OwnerUserId &&
                    !user.IsDeleted &&
                    user.IsActive),
                tenant => tenant.Id,
                user => user.TenantId,
                (tenant, user) => new { Tenant = tenant, User = user })
            .SingleOrDefaultAsync(cancellationToken);

        if (authority == null ||
            _passwordHasher.VerifyHashedPassword(
                authority.User,
                authority.User.PasswordHash,
                password) == PasswordVerificationResult.Failed)
        {
            return CreateError(
                "REPLAY_FAILED",
                "Unable to restore registration. Sign in again.");
        }

        try
        {
            await _userGovernanceService.EnsureTenantLoginAllowedAsync(
                authority.Tenant.Id,
                cancellationToken);
        }
        catch (UserGovernanceException)
        {
            return CreateError(
                "SESSION_RESTORATION_FORBIDDEN",
                "Registration session is no longer current. Sign in again.");
        }

        var (accessToken, refreshToken) = GenerateTokens(authority.User, authority.Tenant.Id);
        await _context.SaveChangesAsync(cancellationToken);

        return new TenantRegistrationResult
        {
            Success = true,
            Response = new TenantRegistrationResponse
            {
                TenantId = authority.Tenant.Id,
                Subdomain = authority.Tenant.Slug!,
                StoreName = authority.Tenant.Name,
                StorefrontUrl = _storefrontUrlResolver.Resolve(authority.Tenant),
                PosUrl = _configuration["Platform:PosUrl"] ?? "https://pos.myplatform.com",
                Status = operation.State == TenantProvisioningState.Ready ? "ready" : "provisioning",
                Message = operation.State == TenantProvisioningState.Ready
                    ? "Your store is ready."
                    : "Your store has been created and provisioning is in progress.",
                User = new RegistrationUserDto
                {
                    Id = authority.User.Id,
                    Email = authority.User.Email,
                    FirstName = authority.User.FirstName,
                    LastName = authority.User.LastName,
                    Role = authority.User.Role.ToString(),
                    TenantId = authority.Tenant.Id
                },
                AccessToken = accessToken,
                RefreshToken = refreshToken
            }
        };
    }

    private static string BuildRequestFingerprint(
        TenantRegistrationRequest request,
        string normalizedEmail,
        string normalizedSubdomain)
    {
        var canonical = string.Join('\n',
            request.BusinessName.Trim(),
            normalizedEmail,
            normalizedSubdomain,
            request.Country?.Trim().ToUpperInvariant() ?? string.Empty,
            string.IsNullOrWhiteSpace(request.Currency)
                ? "EGP"
                : request.Currency.Trim().ToUpperInvariant());

        return HashValue(canonical);
    }

    private static string HashValue(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<SubdomainCheckResponse> CheckSubdomainAvailabilityAsync(
        string subdomain, 
        CancellationToken cancellationToken = default)
    {
        // Validate format first
        if (!StoreSlug.TryNormalize(subdomain, out var normalized, out var errorMessage))
        {
            return new SubdomainCheckResponse
            {
                Available = false,
                Slug = normalized,
                Reason = errorMessage,
                Suggestions = SubdomainValidator.GenerateSuggestions(normalized.Replace("-", ""))
            };
        }
        
        // Check database
        var exists = await _context.Tenants
            .AnyAsync(t => t.Slug != null && t.Slug.ToLower() == normalized, cancellationToken);
        
        if (exists)
        {
            var suggestions = SubdomainValidator.GenerateSuggestions(normalized);
            // Filter out taken suggestions
            var availableSuggestions = new List<string>();
            foreach (var suggestion in suggestions)
            {
                var taken = await _context.Tenants
                    .AnyAsync(t => t.Slug != null && t.Slug.ToLower() == suggestion, cancellationToken);
                if (!taken)
                    availableSuggestions.Add(suggestion);
            }
            
            return new SubdomainCheckResponse
            {
                Available = false,
                Slug = normalized,
                Reason = "This address is already taken",
                Suggestions = availableSuggestions
            };
        }
        
        return new SubdomainCheckResponse
        {
            Available = true,
            Slug = normalized,
            StorefrontUrl = _storefrontUrlResolver.TryResolve(
                new Tenant { Slug = normalized },
                out var storefrontUrl)
                    ? storefrontUrl
                    : null,
            Suggestions = new List<string>()
        };
    }

    public async Task<bool> IsEmailRegisteredAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.ToLowerInvariant().Trim();
        return await _context.Users.AnyAsync(u => u.Email.ToLower() == normalized, cancellationToken);
    }

    private (string accessToken, string refreshToken) GenerateTokens(User user, Guid tenantId)
    {
        var refreshToken = RefreshTokenGenerator.Generate();
        var sessionBinding = RefreshTokenHasher.Hash(refreshToken);
        user.RefreshToken = sessionBinding;
        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays);

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtSettings.Secret);
        
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("firstName", user.FirstName),
            new("lastName", user.LastName),
            new("tenant_id", tenantId.ToString()),
            new("TenantId", tenantId.ToString()), // Legacy claim for backward compatibility
            new(PosSecurityConstants.SessionBindingClaim, sessionBinding)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            Issuer = _jwtSettings.Issuer,
            Audience = _jwtSettings.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var accessToken = tokenHandler.WriteToken(token);

        return (accessToken, refreshToken);
    }

    private static TenantRegistrationResult CreateError(string code, string message, string? field = null)
    {
        return new TenantRegistrationResult
        {
            Success = false,
            Error = new RegistrationErrorResponse
            {
                Error = code,
                Message = message,
                FieldErrors = field != null ? new Dictionary<string, string> { { field, message } } : null
            }
        };
    }

    private static TenantRegistrationResult CreateSubdomainTakenError(string normalizedSubdomain) =>
        new()
        {
            Success = false,
            Error = new RegistrationErrorResponse
            {
                Error = "SUBDOMAIN_TAKEN",
                Message = "This address is already taken",
                FieldErrors = new Dictionary<string, string> { ["subdomain"] = "This address is already taken" },
                Suggestions = StoreSlug.GenerateSuggestions(normalizedSubdomain).ToList()
            }
        };

    private static bool IsUniqueViolation(DbUpdateException exception, string constraintName) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        } postgresException &&
        string.Equals(postgresException.ConstraintName, constraintName, StringComparison.Ordinal);

}
